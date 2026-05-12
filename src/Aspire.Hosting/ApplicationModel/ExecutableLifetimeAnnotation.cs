// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Lifetime modes for executable resources.
/// </summary>
public enum ExecutableLifetime
{
    /// <summary>
    /// Create the resource when the app host process starts and dispose of it when the app host process shuts down.
    /// </summary>
    Session,

    /// <summary>
    /// Attempt to re-use a previously created resource if one exists. Do not destroy the executable on app host process shutdown.
    /// </summary>
    Persistent,
}

/// <summary>
/// Annotation that controls the lifetime of an executable resource.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}")]
public sealed class ExecutableLifetimeAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Gets or sets the lifetime type for the executable resource.
    /// </summary>
    public required ExecutableLifetime Lifetime { get; set; }
}
