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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// What the library made of the file.
/// <para>
/// A refusal is answered rather than thrown, because an import that the owner's
/// grants do not allow is an ordinary outcome for a plugin working through a
/// queue, not an exceptional one. A plugin that had to catch would wrap every
/// item and lose the reason.
/// </para>
/// </summary>
/// <param name="Media">The id the library gave it, or empty when it refused.</param>
public sealed record PluginImportResult(MediaId Media, PluginRefusal? Refusal)
{
    public bool Ok => Refusal is null;
}
