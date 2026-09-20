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
/// A file a plugin has finished with, offered to one of the owner's libraries.
/// <para>
/// The path is relative to a scope the owner granted, never absolute. A plugin
/// that could name any path could import anything on the machine into a library
/// the owner then browses, which is not what granting a download folder meant.
/// </para>
/// </summary>
public sealed record PluginImportRequest
{
    public required LibraryId Library { get; init; }

    public required PluginMediaKind Kind { get; init; }

    /// <summary>Relative to the granted scope, forward slashes.</summary>
    public required string SourcePath { get; init; }

    /// <summary>
    /// What the guide said was on, for a recording. Null for everything else,
    /// because nothing else has a guide to be identified by.
    /// </summary>
    public PluginEpgProgram? Program { get; init; }

    /// <summary>
    /// What the plugin already knows, so the server does not re-ask a provider
    /// for an answer the plugin was told by the source it downloaded from.
    /// </summary>
    public string? Title { get; init; }

    public int? Year { get; init; }

    /// <summary>
    /// Whether the file moves into the library or is copied there. A torrent
    /// still seeding must not be moved out from under its own client, so the
    /// default leaves it where it is.
    /// </summary>
    public bool Move { get; init; }
}
