// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
// -----------------------------------------------------------------------------

using Microsoft.Extensions.DependencyInjection;

namespace NoMercy.PluginSdk;

/// <summary>
/// One plugin's own container, with the host behind it.
/// <para>
/// What the plugin registered is answered from its own container. Everything
/// else falls through to the host, so a plugin still reaches the facades the
/// server offers it. Nothing goes the other way: the host cannot see what a
/// plugin registered, and neither can another plugin.
/// </para>
/// <para>
/// Disposed when the plugin unloads, before its load context goes. A
/// disposable whose assembly has already been unloaded crashes in the
/// finalizer.
/// </para>
/// </summary>
internal sealed class PluginServiceProvider(ServiceProvider own, IServiceProvider host)
    : IServiceProvider,
        IDisposable
{
    public object? GetService(Type serviceType) =>
        own.GetService(serviceType) ?? host.GetService(serviceType);

    public void Dispose() => own.Dispose();
}

/// <summary>
/// The host's registration list, kept so a plugin's own container can hand
/// those service types back to the host instead of building second copies.
/// <para>
/// The list is the live collection, read after the host provider is built and
/// therefore complete. Registering it as an instance is what makes that
/// possible: a factory would need the provider that is being described.
/// </para>
/// </summary>
internal sealed class PluginHostServiceCollection(IServiceCollection services)
{
    public IServiceCollection Services { get; } = services;
}
