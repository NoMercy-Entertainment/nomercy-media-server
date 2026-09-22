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

namespace NoMercy.Plugin.Samples.ManifestFailure;

// The ONLY IPlugin type in this assembly, deliberately — PluginLoader's
// manifest-driven load path keys its registry entry by manifest.Id (one
// logical plugin per manifest), unlike the direct-assembly path which
// isolates each type independently. A multi-type assembly staged via a
// manifest is out of that contract, so this fixture stays single-type to
// cleanly and deterministically exercise "a manifest-declared, auto-enabled
// plugin whose Initialize() throws" without depending on Assembly.GetTypes()
// ordering across several types.
public sealed class ManifestAutoEnableInitializeThrowsPlugin : IPlugin
{
    public static readonly Ulid FixedId = Ulid.Parse("01MAN0FESTFA000RE000000000");

    public string Name => "ManifestFailure";
    public string Description =>
        "Constructs fine, Initialize() throws when auto-enabled by manifest";
    public Ulid Id => FixedId;
    public Version Version { get; } = new(0, 1, 0);

    public void Initialize(IPluginContext context) =>
        throw new InvalidOperationException(
            "ManifestAutoEnableInitializeThrowsPlugin: initialize boom"
        );

    public void Dispose() { }
}
