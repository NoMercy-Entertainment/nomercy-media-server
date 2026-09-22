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

// Complements InitializeThrowsPlugin: Initialize() still throws (so the loader
// still marks it Malfunctioned), but Dispose() succeeds cleanly this time —
// exercises PluginLoader's disposal-after-failure try block completing
// normally, distinct from InitializeThrowsPlugin's own Dispose() also
// throwing into the nested disposeEx catch.
public sealed class InitializeThrowsDisposeSucceedsPlugin : IPlugin
{
    public static readonly Ulid FixedId = Ulid.Parse("01SAMPLE000000000000000004");

    public string Name => "InitializeThrowsDisposeSucceeds";
    public string Description => "Constructs fine, Initialize throws, Dispose succeeds";
    public Ulid Id => FixedId;
    public Version Version { get; } = new(0, 1, 0);

    public void Initialize(IPluginContext context) =>
        throw new InvalidOperationException(
            "InitializeThrowsDisposeSucceedsPlugin: initialize boom"
        );

    public void Dispose() { }
}
