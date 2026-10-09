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

using System.Reflection;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;
using NoMercy.MediaProcessing.Jobs.PaletteJobs;

namespace NoMercy.Tests.MediaProcessing.Palettes;

[Trait("Category", "Unit")]
public class PaletteBackfillPagingTests
{
    [Theory]
    [InlineData("artist")]
    [InlineData("album")]
    [InlineData("track")]
    [InlineData("playlist")]
    [InlineData("releasegroup")]
    public async Task Painting_each_batch_dispatches_every_pending_guid(string entityType)
    {
        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using MediaContext db = new(options);
        Dictionary<Guid, ColorPaletteTimeStamps> entities = new();
        Library library = new()
        {
            Id = Ulid.NewUlid(),
            Title = "test",
            Type = "music",
        };
        Folder folder = new() { Id = Ulid.NewUlid() };

        for (int index = 0; index < 401; index++)
        {
            Guid id = Guid.NewGuid();
            ColorPaletteTimeStamps entity = entityType switch
            {
                "artist" => new Artist
                {
                    Id = id,
                    Name = index.ToString(),
                    HostFolder = "",
                },
                "album" => new Album
                {
                    Id = id,
                    Name = index.ToString(),
                    LibraryId = library.Id,
                    Library = library,
                    FolderId = folder.Id,
                    LibraryFolder = folder,
                },
                "track" => new Track { Id = id, Name = index.ToString() },
                "playlist" => new Playlist { Id = id, Name = index.ToString() },
                _ => new ReleaseGroup { Id = id, Title = index.ToString() },
            };
            db.Add(entity);
            entities.Add(id, entity);
        }
        await db.SaveChangesAsync();

        MethodInfo query = typeof(PaletteBackfillJob).GetMethod(
            "GetGuidPendingRowsAsync",
            BindingFlags.NonPublic | BindingFlags.Static
        )!;
        int offset = 0;
        HashSet<Guid> dispatched = [];
        for (int pass = 0; pass < 3; pass++)
        {
            object?[] arguments =
                query.GetParameters().Length == 3 ? [db, entityType, offset] : [db, entityType];
            Task<List<(Guid Id, string? Palette)>> task =
                (Task<List<(Guid Id, string? Palette)>>)query.Invoke(null, arguments)!;
            List<(Guid Id, string? Palette)> rows = await task;
            foreach ((Guid id, string? _) in rows)
            {
                dispatched.Add(id);
                entities[id]._colorPalette = "painted";
            }
            await db.SaveChangesAsync();
            offset += rows.Count;
        }

        dispatched.Should().HaveCount(401);
        entities.Values.Should().OnlyContain(entity => entity._colorPalette == "painted");
    }
}
