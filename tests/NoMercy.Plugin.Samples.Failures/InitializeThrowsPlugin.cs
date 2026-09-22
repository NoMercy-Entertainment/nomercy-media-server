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

// Real assembly fixture: constructs fine (so SafePluginIdentity can read a real
// non-empty Id) but Initialize() throws, and Dispose() also throws while the
// loader is cleaning up after that failure. Exercises PluginLoader's
// identity-known malfunction recording AND the nested dispose-failure log path.
public sealed class InitializeThrowsPlugin : IPlugin
{
    public static readonly Ulid FixedId = Ulid.Parse("01SAMPLE000000000000000002");

    public string Name => "InitializeThrows";
    public string Description => "Constructs fine, Initialize/Dispose both throw";
    public Ulid Id => FixedId;
    public Version Version { get; } = new(0, 1, 0);

    public void Initialize(IPluginContext context) =>
        throw new InvalidOperationException("InitializeThrowsPlugin: initialize boom");

    public void Dispose() =>
        throw new InvalidOperationException("InitializeThrowsPlugin: dispose boom");
}
