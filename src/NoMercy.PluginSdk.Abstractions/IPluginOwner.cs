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
/// Who owns this server, for the questions only the owner can answer: what
/// was purchased, and whether a file may be installed by hand.
/// <para>
/// A seam rather than a direct read of the user list, because the plugin
/// platform does not reference it. A host that registers nothing answers
/// <see cref="Guid.Empty"/>, which holds no entitlements, so the safe answer
/// is the default rather than something to remember to configure.
/// </para>
/// </summary>
public interface IPluginOwner
{
    Guid Id { get; }
}
