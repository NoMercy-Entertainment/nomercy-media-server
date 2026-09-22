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
using Newtonsoft.Json;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.Samples.Failures;

// Real assembly fixture: a healthy plugin that ALSO implements
// IPluginServiceRegistrator, so it is discoverable both through
// PluginManager.GetServiceRegistrators() (loaded, active instance) and through
// PluginServiceCollectionExtensions.RegisterPluginServicesFromManifests
// (reflection-only discovery against the manifest-described assembly).
//
// Initialize() touches Newtonsoft.Json — a dependency bundled alongside this
// assembly but deliberately NOT in PluginHostOptions.DefaultSharedAssemblies —
// forcing PluginLoadContext.Load() to actually resolve and load it rather than
// every dependency being intercepted by the shared-assembly short-circuit.
public sealed class ServiceRegistratorPlugin : IPlugin, IPluginServiceRegistrator
{
    public static readonly Ulid FixedId = Ulid.Parse("01SAMPLE000000000000000003");

    public string Name => "ServiceRegistrator";
    public string Description => "Healthy plugin that also registers a host service";
    public Ulid Id => FixedId;
    public Version Version { get; } = new(0, 1, 0);

    public void Initialize(IPluginContext context) =>
        JsonConvert.SerializeObject(new { ok = true });

    public void RegisterServices(IServiceCollection services) =>
        services.AddSingleton(new FailuresPluginMarker());

    public void Dispose() { }
}
