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

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.Samples.Failures;

// Real assembly fixture: its constructor throws, so PluginInstanceFactory.Create
// never returns an instance for this type. Exercises PluginLoader's
// identity-unknown recovery path (SafePluginIdentity.Read(null, pluginType)),
// where the failed plugin can never be re-added to the registry under a real id.
public sealed class ConstructorThrowsPlugin : IPlugin
{
    public ConstructorThrowsPlugin() =>
        throw new InvalidOperationException("ConstructorThrowsPlugin: constructor boom");

    public string Name => "ConstructorThrows";
    public string Description => "Never constructed";
    public Ulid Id => Ulid.Parse("01SAMPLE000000000000000001");
    public Version Version { get; } = new(0, 1, 0);

    public void Initialize(IPluginContext context) { }

    public void Dispose() { }
}
