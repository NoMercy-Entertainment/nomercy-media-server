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

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Playlists;
using NoMercy.Queue.MediaServer.Jobs;
using Xunit;

namespace NoMercy.Tests.Queue;

public class ImageReferenceReaderTests : IDisposable
{
    private static readonly string[] ImageWords =
    [
        "Poster",
        "Backdrop",
        "Logo",
        "Image",
        "Still",
        "Profile",
        "Cover",
        "Thumb",
        "Banner",
        "Icon",
        "Photo",
        "Avatar",
        "Picture",
        "Artwork",
        "Background",
    ];

    private static readonly Dictionary<string, string> NotInOriginal = new()
    {
        ["Track.Cover"] =
            "music cover: its file lives in images/music, which the sweep never checks",
        ["Album.Cover"] =
            "music cover: its file lives in images/music, which the sweep never checks",
        ["Artist.Cover"] =
            "music cover: its file lives in images/music, which the sweep never checks",
        ["ReleaseGroup.Cover"] =
            "music cover: its file lives in images/music, which the sweep never checks",
        ["Playlist.Cover"] =
            "music cover: its file lives in images/music, which the sweep never checks",
        ["VideoFileAudioTrack.Profile"] = "the name of an audio profile, not an image",
        ["EncodingHistory.ProfileName"] = "the name of an encoding profile, not an image",
        ["EncodingPreset.ProfileJson"] = "an encoding profile as JSON, not an image",
    };

    private readonly SqliteConnection _connection = new("DataSource=:memory:;Foreign Keys=False");
    private readonly DbContextOptions<MediaContext> _options;

    public ImageReferenceReaderTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;
        using MediaContext init = new(_options);
        init.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private ImageReferenceReader CreateReader()
    {
        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));
        return new(factory.Object);
    }

    private List<string> ImageLikeProperties()
    {
        using MediaContext context = new(_options);
        List<string> found = [];
        foreach (IEntityType entity in context.Model.GetEntityTypes())
        foreach (IProperty property in entity.GetProperties())
        {
            if (property.ClrType != typeof(string) || property.PropertyInfo is null)
                continue;

            if (ImageWords.Any(word => property.Name.Contains(word, StringComparison.Ordinal)))
                found.Add($"{entity.ClrType.Name}.{property.Name}");
        }

        return found.Order().ToList();
    }

    [Fact]
    public void Columns_ListEveryImagePathColumnOfTheModel()
    {
        List<string> listed = ImageReferenceReader
            .Columns.Select(column => $"{column.Entity.Name}.{column.Property}")
            .ToList();

        List<string> missing = ImageLikeProperties()
            .Where(name => !listed.Contains(name) && !NotInOriginal.ContainsKey(name))
            .ToList();

        missing
            .Should()
            .BeEmpty(
                "a column that names an image must be read, or the orphan sweep deletes "
                    + "the file an item still shows; list it in ImageReferenceReader.Columns "
                    + "or, when its file is not in original/, in NotInOriginal with the reason. "
                    + "Missing: {0}",
                string.Join(", ", missing)
            );
    }

    [Fact]
    public void NotInOriginal_NamesOnlyRealPropertiesTheReaderDoesNotList()
    {
        List<string> onModel = ImageLikeProperties();
        List<string> listed = ImageReferenceReader
            .Columns.Select(column => $"{column.Entity.Name}.{column.Property}")
            .ToList();

        NotInOriginal.Keys.Should().OnlyContain(name => onModel.Contains(name));
        NotInOriginal.Keys.Should().NotContain(name => listed.Contains(name));
    }

    [Fact]
    public async Task ReadAsync_ReadsALibraryImageAndAUserPlaylistCover()
    {
        await using (MediaContext context = new(_options))
        {
            context.Libraries.Add(
                new()
                {
                    Id = Ulid.NewUlid(),
                    Title = "Movies",
                    Type = "movie",
                    Image = "/library-image.jpg",
                }
            );
            context.UserPlaylists.Add(
                new()
                {
                    Name = "Mix",
                    UserId = Guid.NewGuid(),
                    Cover = "/playlist-cover.jpg",
                }
            );
            await context.SaveChangesAsync();
        }

        HashSet<string> names = await CreateReader().ReadAsync(CancellationToken.None);

        names.Should().Contain(["library-image.jpg", "playlist-cover.jpg"]);
    }

    [Fact]
    public async Task ReadAsync_ReadsTheCandidatePostersOfAnInboxItem()
    {
        await using (MediaContext context = new(_options))
        {
            InboxItem item = new()
            {
                Id = Ulid.NewUlid(),
                DriverId = Ulid.NewUlid(),
                Candidates =
                [
                    new()
                    {
                        Provider = "tmdb",
                        ExternalId = "1",
                        Title = "One",
                        PosterPath = "/candidate-one.jpg",
                    },
                ],
                SelectedMatch = new()
                {
                    Provider = "tmdb",
                    ExternalId = "2",
                    Title = "Two",
                    PosterPath = "/selected-two.jpg",
                },
            };
            context.InboxItems.Add(item);
            await context.SaveChangesAsync();
        }

        HashSet<string> names = await CreateReader().ReadAsync(CancellationToken.None);

        names.Should().Contain(["candidate-one.jpg", "selected-two.jpg"]);
    }
}
