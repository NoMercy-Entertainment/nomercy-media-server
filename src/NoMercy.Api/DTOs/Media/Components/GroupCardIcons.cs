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

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>
/// The Moooom icon a group card shows, chosen by group name. Ported from the
/// web app's genreColors table (src/config/global.ts) so every client gets the
/// same icon for the same name.
/// </summary>
public static class GroupCardIcons
{
    public const string DefaultIcon = "bat";

    private static readonly Dictionary<string, string> Icons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Action & Adventure"] = "lightning",
        ["Action"] = "muscle",
        ["Adventure"] = "mapClue",
        ["Animation"] = "designerTools",
        ["Comedy"] = "openSmile",
        ["Crime"] = "chefKnife",
        ["Documentary"] = "folderOpen",
        ["Drama"] = "sweatSmile",
        ["Family"] = "multiUsers",
        ["Fantasy"] = "witchHat",
        ["History"] = "paperScroll",
        ["Horror"] = "ghost",
        ["Kids"] = "mouse",
        ["Music"] = "noteSixteenthPair",
        ["Mystery"] = "footPrint",
        ["News"] = "globe",
        ["Reality"] = "eye",
        ["Romance"] = "bubbles",
        ["Sci-Fi & Fantasy"] = "ussEnterprise",
        ["Science Fiction"] = "rocket",
        ["Soap"] = "bubbles",
        ["Talk"] = "megaphone",
        ["Thriller"] = "searchMagnifyingGlass",
        ["TV Movie"] = "tv",
        ["War & Politics"] = "medal",
        ["War"] = "gun",
        ["Western"] = "cowboyHat",
    };

    public static string For(string name) => Icons.GetValueOrDefault(name, DefaultIcon);
}
