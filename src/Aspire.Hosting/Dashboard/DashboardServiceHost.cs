// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Dashboard;

/// <summary>
/// Hosts a gRPC service via <see cref="DashboardService"/> (aka the "Resource Service") that a dashboard can connect to.
/// Configures DI and networking options for the service.
/// </summary>
internal sealed class DashboardServiceHost : IHostedService
{
    /// <summary>
    /// Name of the environment variable that optionally specifies the resource service URL,
    /// which the dashboard will connect to over gRPC.
    /// </summary>
    /// <remarks>
    /// This is primarily intended for cases outside of the local developer environment.
    /// If no value exists for this variable, a port is assigned dynamically.
    /// </remarks>
    private const string DashboardServiceUrlVariableName = "DOTNET_DASHBOARD_GRPC_ENDPOINT_URL";

    /// <summary>
    /// Provides access to the URI at which the resource service endpoint is hosted.
    /// </summary>
    private readonly TaskCompletionSource<string> _resourceServiceUri = new();

    /// <summary>
    /// <see langword="null"/> if <see cref="DistributedApplicationOptions.DashboardEnabled"/> is <see langword="false"/>.
    /// </summary>
    private readonly WebApplication? _app;
    private readonly ILogger<DashboardServiceHost> _logger;

    public DashboardServiceHost(
        DistributedApplicationOptions options,
        DistributedApplicationModel applicationModel,
        KubernetesService kubernetesService,
        IOptions<PublishingOptions> publishingOptions,
        ILoggerFactory loggerFactory,
        ILogger<DashboardServiceHost> logger,
        IConfigureOptions<LoggerFilterOptions> loggerOptions)
    {
        _logger = logger;

        if (!options.DashboardEnabled ||
            publishingOptions.Value.Publisher == "manifest") // HACK: Manifest publisher check is temporary until DcpHostService is integrated with DcpPublisher.
        {
            _resourceServiceUri.SetCanceled();
            return;
        }

        try
        {
            var builder = WebApplication.CreateBuilder();

            // Logging
            builder.Services.AddSingleton(loggerFactory);
            builder.Services.AddSingleton(loggerOptions);
            builder.Services.Add(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(Logger<>)));

            builder.Services.AddGrpc();
            builder.Services.AddSingleton(applicationModel);
            builder.Services.AddSingleton(kubernetesService);
            builder.Services.AddSingleton<DashboardServiceData>();

            builder.WebHost.ConfigureKestrel(ConfigureKestrel);

            _app = builder.Build();

            _app.MapGrpcService<DashboardService>();
        }
        catch (Exception ex)
        {
            _resourceServiceUri.TrySetException(ex);
            throw;
        }

        return;

        static void ConfigureKestrel(KestrelServerOptions kestrelOptions)
        {
            // Check env var for URLs to listen on.
            var uris = EnvironmentUtil.GetAddressUris(DashboardServiceUrlVariableName, defaultValue: null);

            string? scheme;

            if (uris is null or [])
            {
                // No URI available from the environment.
                scheme = null;

                // Listen on a random port.
                kestrelOptions.Listen(IPAddress.Loopback, port: 0, ConfigureListen);
            }
            else if (uris is [Uri uri])
            {
                if (!uri.IsLoopback)
                {
                    throw new ArgumentException($"{DashboardServiceUrlVariableName} must contain a local loopback address.");
                }

                scheme = uri.Scheme;

                kestrelOptions.ListenLocalhost(uri.Port, ConfigureListen);

            }
            else
            {
                throw new ArgumentException($"Multiple URIs are not supported in the {DashboardServiceUrlVariableName} environment variable.");
            }

            void ConfigureListen(ListenOptions options)
            {
                // Force HTTP/2 for gRPC, so that it works over non-TLS connections
                // which cannot negotiate between HTTP/1.1 and HTTP/2.
                options.Protocols = HttpProtocols.Http2;

                if (string.Equals(scheme, "https", StringComparison.Ordinal))
                {
                    options.UseHttps();
                }
            }
        }
    }

    /// <summary>
    /// Gets the URI upon which the resource service is listening.
    /// </summary>
    /// <remarks>
    /// Intended to be used by the app model when launching the dashboard process, populating its
    /// <c>DOTNET_DASHBOARD_GRPC_ENDPOINT_URL</c> environment variable with a single URI.
    /// </remarks>
    public async Task<string> GetResourceServiceUriAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var uri = await _resourceServiceUri.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        stopwatch.Stop();

        if (stopwatch.Elapsed > TimeSpan.FromSeconds(2))
        {
            _logger.LogWarning("Unexpectedly long wait for resource service URI ({elapsed}).", stopwatch.Elapsed);
        }

        return uri;
    }

    async Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            await _app.StartAsync(cancellationToken).ConfigureAwait(false);

            var addressFeature = _app.Services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>();

            if (addressFeature is null)
            {
                _resourceServiceUri.SetException(new InvalidOperationException("Could not obtain IServerAddressesFeature. Dashboard URIs are not available."));
                return;
            }

            _resourceServiceUri.SetResult(addressFeature.Addresses.Single());
        }
    }

    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        _resourceServiceUri.TrySetCanceled(cancellationToken);

        if (_app is not null)
        {
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
