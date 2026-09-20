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

/// <summary>What happened to one item in a library.</summary>
public enum PluginLibraryChangeKind
{
    Added,
    Updated,
    Removed,
}

/// <param name="Media">The item. Still meaningful for a removal, so a plugin can forget what it cached.</param>
public sealed record PluginLibraryChange(
    LibraryId Library,
    MediaId Media,
    PluginMediaKind Kind,
    PluginLibraryChangeKind Change,
    DateTimeOffset At
);
