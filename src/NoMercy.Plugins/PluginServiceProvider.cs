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
/// One plugin's own container, with the host used only to seed it.
/// <para>
/// What the plugin registered is answered from its own container, and so is
/// everything <see cref="PluginInstanceFactory.ChildContainer"/> chose to
/// forward from the host ahead of time. There is no live fallback to the host
/// here: the v3 SDK contract is that a plugin reaches the host only through
/// what the host deliberately forwarded, never through whatever else the host
/// container happens to hold. A live <c>?? host.GetService(...)</c> would
/// undo that allowlist for anything <c>own</c> does not already carry. Nothing
/// goes the other way either: the host cannot see what a plugin registered,
/// and neither can another plugin.
/// </para>
/// <para>
/// Disposed when the plugin unloads, before its load context goes. A
/// disposable whose assembly has already been unloaded crashes in the
/// finalizer.
/// </para>
/// </summary>
internal sealed class PluginServiceProvider(ServiceProvider own) : IServiceProvider, IDisposable
{
    public object? GetService(Type serviceType) => own.GetService(serviceType);

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
