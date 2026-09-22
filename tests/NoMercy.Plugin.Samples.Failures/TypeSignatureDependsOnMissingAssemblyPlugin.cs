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

using Newtonsoft.Json.Linq;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.Samples.Failures;

// A field of a non-shared, bundled dependency type in this class's OWN
// metadata signature (as opposed to ServiceRegistratorPlugin, which only
// references Newtonsoft.Json inside a method body). Assembly.GetTypes() must
// eagerly resolve every type's field signatures, so when this fixture is
// staged WITHOUT Newtonsoft.Json.dll alongside it, GetTypes() throws
// ReflectionTypeLoadException for this specific type while still succeeding
// for every other type in the module.
public sealed class TypeSignatureDependsOnMissingAssemblyPlugin : IPlugin
{
    public JObject? PayloadField;

    public string Name => "TypeSignatureDependsOnMissingAssembly";
    public string Description => "d";
    public Ulid Id => Ulid.Parse("01SAMPLE000000000000000005");
    public Version Version { get; } = new(0, 1, 0);

    public void Initialize(IPluginContext context) { }

    public void Dispose() { }
}
