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

using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Notifications.Push;

namespace NoMercy.Data.Notifications;

/// <summary>
/// Implements <see cref="IPlayableMediaProbe"/> for NoMercy.Notifications, which
/// cannot reference NoMercy.Database itself. A movie is playable once it has any
/// VideoFile; a show is playable once any of its episodes has one.
///
/// The media types here ("movie" / "tvshow") match the literal values
/// MovieImportJob / ShowImportJob put on MediaAddedEvent.MediaType, not the
/// "tv" used by NoMercy.NmSystem.Domain.MediaTypes elsewhere.
/// </summary>
public class PlayableMediaProbe(IDbContextFactory<MediaContext> contextFactory)
    : IPlayableMediaProbe
{
    public async Task<bool> HasPlayableVideoAsync(
        string mediaType,
        int mediaId,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        return mediaType switch
        {
            "movie" => await context
                .VideoFiles.AsNoTracking()
                .AnyAsync(file => file.MovieId == mediaId, ct),
            "tvshow" => await context
                .VideoFiles.AsNoTracking()
                .AnyAsync(file => file.Episode!.TvId == mediaId, ct),
            _ => false,
        };
    }
}
