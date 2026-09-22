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
/// One entry of a channel's guide, in the words every client draws.
///
/// Start and stop carry their offset. A guide written in the server's local
/// time read three hours wrong for anyone watching from another country, and
/// nothing about the row said so.
/// </summary>
public sealed record PluginEpgProgramme(
    string ProgrammeId,
    string ChannelId,
    string Title,
    string? Description,
    DateTimeOffset Start,
    DateTimeOffset Stop,
    string? Category,
    string? AgeRating,
    Uri? Image
);
