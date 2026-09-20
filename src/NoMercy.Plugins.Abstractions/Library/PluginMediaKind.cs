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
/// What a plugin is handing the library.
/// <para>
/// <see cref="File" /> is the honest answer when the plugin does not know, and
/// the server works it out the way it does for anything the owner drops in a
/// folder. A plugin that guesses wrong files a film under episodes, and the
/// owner has to undo it by hand.
/// </para>
/// </summary>
public enum PluginMediaKind
{
    File,
    Movie,
    Show,
    Episode,
    Album,
    Track,
    Recording,
}
