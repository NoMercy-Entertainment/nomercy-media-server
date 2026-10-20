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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// How one <see cref="IPluginAudioTools.RunFilterGraphAsync(PluginAudioInput, PluginFilterGraph, Action{string}?, Action{string}?, PluginAudioRunOptions, CancellationToken)" />
/// may differ from the default. Added in ABI 12.6.
/// </summary>
/// <param name="Timeout">
/// How long the run may take before the host ends it. Null is the host's
/// default of ten minutes; anything above sixty minutes is clamped to sixty.
/// A five-minute track through whisper large-v3 on a CPU takes about twelve.
/// </param>
public sealed record PluginAudioRunOptions(TimeSpan? Timeout)
{
    /// <summary>The host's default: ten minutes.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The most a run may ask for: sixty minutes.</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(60);
}
