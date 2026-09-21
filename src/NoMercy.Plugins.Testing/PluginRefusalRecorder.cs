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

using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Testing;

/// <summary>
/// Every refusal the fakes raised, in order.
/// <para>
/// Kept rather than only thrown, because the interesting assertion is usually
/// which refusal a plugin provoked and not merely that something threw. A test
/// that catches the exception and checks nothing passes when the wrong rule
/// fired.
/// </para>
/// </summary>
public sealed class PluginRefusalRecorder
{
    private readonly List<PluginRefusal> _refusals = [];

    public IReadOnlyList<PluginRefusal> Refusals => _refusals;

    /// <summary>Records it and throws it, so the plugin sees what the host would give it.</summary>
    public PluginRefusedException Raise(PluginRefusal refusal)
    {
        _refusals.Add(refusal);
        return new PluginRefusedException(refusal);
    }

    public void Clear() => _refusals.Clear();
}
