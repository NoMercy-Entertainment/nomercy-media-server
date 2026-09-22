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

namespace NoMercy.PluginSdk.Revocation;

/// <summary>One build that must not run, and why.</summary>
/// <param name="Hash">
/// The package hash, so a revocation names one build rather than a plugin. A
/// publisher who ships a fix should not have to ask for the revocation to be
/// lifted before anyone can install it.
/// </param>
/// <param name="Reason">An i18n key, never a sentence: the clients render it.</param>
public sealed record PluginRevocationEntry(Ulid PluginId, string Hash, string Reason);
