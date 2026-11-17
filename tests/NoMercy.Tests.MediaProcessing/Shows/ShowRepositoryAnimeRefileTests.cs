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

using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.TvShows;
using NoMercy.MediaProcessing.Shows;

namespace NoMercy.Tests.MediaProcessing.Shows;

/// <summary>
/// The anime enrichment pass re-files a show it proves is anime (#105). A show
/// belongs to one library, so the re-file must move the LibraryTv link along
/// with Tvs.LibraryId; moving only the column left the show listed under the
/// library it was just taken out of.
/// </summary>
[Trait("Category", "Unit")]
public class ShowRepositoryAnimeRefileTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    private readonly Library _animeLibrary = new()
    {
        Id = Ulid.NewUlid(),
        Type = "anime",
        Title = "Anime",
    };

    private readonly Library _tvLibrary = new()
    {
        Id = Ulid.NewUlid(),
        Type = "tv",
        Title = "TV Shows",
    };

    public ShowRepositoryAnimeRefileTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext context = new(_options);
        context.Database.EnsureCreated();
        context.Libraries.AddRange(_animeLibrary, _tvLibrary);
        context.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task AddShowOrder_LinkThenRefile_LeavesOnlyTheLinkToTvsLibraryId()
    {
        Tv show = await AddShowAsync(1, _tvLibrary.Id);

        // ShowManager.AddShowAsync links to the resolved library first, then
        // the enrichment pass re-files the show once it proves it is anime.
        await using (MediaContext linkContext = new(_options))
        {
            await new ShowRepository(linkContext).LinkToLibrary(_tvLibrary, show);
        }

        bool moved;
        await using (MediaContext refileContext = new(_options))
        {
            moved = await new ShowRepository(refileContext).EnsureFiledUnderLibraryTypeAsync(
                show.Id,
                "anime"
            );
        }

        moved.Should().BeTrue();
        (await LibraryIdOfAsync(show.Id)).Should().Be(_animeLibrary.Id);
        (await LinksOfAsync(show.Id))
            .Select(link => link.LibraryId)
            .Should()
            .Equal(_animeLibrary.Id);
    }

    [Fact]
    public async Task Refile_KeepsTheOriginAndDateTheLinkWasMadeWith()
    {
        Tv show = await AddShowAsync(2, _tvLibrary.Id);
        DateTime addedAt = new(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        await using (MediaContext seedContext = new(_options))
        {
            seedContext.LibraryTv.Add(
                new(_tvLibrary.Id, show.Id, LibraryLinkOrigin.Manual) { AddedAt = addedAt }
            );
            await seedContext.SaveChangesAsync();
        }

        await using (MediaContext refileContext = new(_options))
        {
            await new ShowRepository(refileContext).EnsureFiledUnderLibraryTypeAsync(
                show.Id,
                "anime"
            );
        }

        List<LibraryTv> links = await LinksOfAsync(show.Id);
        links.Should().ContainSingle();
        links[0].LibraryId.Should().Be(_animeLibrary.Id);
        links[0].AddedBy.Should().Be(LibraryLinkOrigin.Manual);
        links[0].AddedAt.Should().Be(addedAt);
    }

    [Fact]
    public async Task ShowAlreadyFiledAsAnime_WithItsLinkLeftBehind_GetsTheLinkMoved()
    {
        // The state the column-only re-file left in existing databases.
        Tv show = await AddShowAsync(3, _animeLibrary.Id);
        await using (MediaContext seedContext = new(_options))
        {
            seedContext.LibraryTv.Add(new(_tvLibrary.Id, show.Id));
            await seedContext.SaveChangesAsync();
        }

        bool changed;
        await using (MediaContext refileContext = new(_options))
        {
            changed = await new ShowRepository(refileContext).EnsureFiledUnderLibraryTypeAsync(
                show.Id,
                "anime"
            );
        }

        changed.Should().BeTrue();
        (await LinksOfAsync(show.Id))
            .Select(link => link.LibraryId)
            .Should()
            .Equal(_animeLibrary.Id);
    }

    [Fact]
    public async Task ShowAlreadyInPlace_IsLeftAlone()
    {
        Tv show = await AddShowAsync(4, _animeLibrary.Id);
        await using (MediaContext seedContext = new(_options))
        {
            seedContext.LibraryTv.Add(new(_animeLibrary.Id, show.Id, LibraryLinkOrigin.Manual));
            await seedContext.SaveChangesAsync();
        }

        bool changed;
        await using (MediaContext refileContext = new(_options))
        {
            changed = await new ShowRepository(refileContext).EnsureFiledUnderLibraryTypeAsync(
                show.Id,
                "anime"
            );
        }

        changed.Should().BeFalse();
        List<LibraryTv> links = await LinksOfAsync(show.Id);
        links.Should().ContainSingle();
        links[0].AddedBy.Should().Be(LibraryLinkOrigin.Manual);
    }

    [Fact]
    public async Task ShowWithoutAnyLink_IsMoved_ButGainsNoMembership()
    {
        Tv show = await AddShowAsync(5, _tvLibrary.Id);

        await using (MediaContext refileContext = new(_options))
        {
            await new ShowRepository(refileContext).EnsureFiledUnderLibraryTypeAsync(
                show.Id,
                "anime"
            );
        }

        (await LibraryIdOfAsync(show.Id)).Should().Be(_animeLibrary.Id);
        (await LinksOfAsync(show.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task NoLibraryOfThatType_ChangesNothing()
    {
        Tv show = await AddShowAsync(6, _tvLibrary.Id);
        await using (MediaContext seedContext = new(_options))
        {
            seedContext.LibraryTv.Add(new(_tvLibrary.Id, show.Id));
            await seedContext.SaveChangesAsync();
        }

        bool changed;
        await using (MediaContext refileContext = new(_options))
        {
            changed = await new ShowRepository(refileContext).EnsureFiledUnderLibraryTypeAsync(
                show.Id,
                "documentary"
            );
        }

        changed.Should().BeFalse();
        (await LibraryIdOfAsync(show.Id)).Should().Be(_tvLibrary.Id);
        (await LinksOfAsync(show.Id)).Select(link => link.LibraryId).Should().Equal(_tvLibrary.Id);
    }

    [Fact]
    public async Task TargetLinkAlreadyThere_TakesOverTheOriginAndDateOfTheLinksItReplaces()
    {
        Tv show = await AddShowAsync(7, _tvLibrary.Id);
        DateTime earliest = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await using (MediaContext seedContext = new(_options))
        {
            seedContext.LibraryTv.Add(
                new(_animeLibrary.Id, show.Id, LibraryLinkOrigin.File)
                {
                    AddedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                }
            );
            seedContext.LibraryTv.Add(
                new(_tvLibrary.Id, show.Id, LibraryLinkOrigin.Manual) { AddedAt = earliest }
            );
            await seedContext.SaveChangesAsync();
        }

        await using (MediaContext refileContext = new(_options))
        {
            await new ShowRepository(refileContext).EnsureFiledUnderLibraryTypeAsync(
                show.Id,
                "anime"
            );
        }

        List<LibraryTv> links = await LinksOfAsync(show.Id);
        links.Should().ContainSingle();
        links[0].LibraryId.Should().Be(_animeLibrary.Id);
        links[0].AddedBy.Should().Be(LibraryLinkOrigin.Manual);
        links[0].AddedAt.Should().Be(earliest);
    }

    [Fact]
    public async Task ScanLinksTheTargetBetweenReadAndWrite_RefileStillCompletes()
    {
        Tv show = await AddShowAsync(8, _tvLibrary.Id);
        await using (MediaContext seedContext = new(_options))
        {
            seedContext.LibraryTv.Add(new(_tvLibrary.Id, show.Id, LibraryLinkOrigin.Manual));
            await seedContext.SaveChangesAsync();
        }

        // Stands in for a concurrent LinkToLibrary: the anime link appears
        // after the re-file has read the links, just before its first write.
        LinkBeforeFirstWriteInterceptor scan = new(_animeLibrary.Id, show.Id);
        DbContextOptions<MediaContext> racingOptions = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(_connection)
            .AddInterceptors(scan)
            .Options;

        bool moved;
        await using (MediaContext refileContext = new(racingOptions))
        {
            moved = await new ShowRepository(refileContext).EnsureFiledUnderLibraryTypeAsync(
                show.Id,
                "anime"
            );

            // Nothing may be left pending for the import's next save.
            refileContext.ChangeTracker.HasChanges().Should().BeFalse();
        }

        scan.Fired.Should().BeTrue();
        moved.Should().BeTrue();
        (await LibraryIdOfAsync(show.Id)).Should().Be(_animeLibrary.Id);
        List<LibraryTv> links = await LinksOfAsync(show.Id);
        links.Should().ContainSingle();
        links[0].LibraryId.Should().Be(_animeLibrary.Id);
        links[0].AddedBy.Should().Be(LibraryLinkOrigin.Manual);
    }

    [Fact]
    public async Task InsideACallersTransaction_TheCallerDecidesWhetherItSticks()
    {
        Tv show = await AddShowAsync(9, _tvLibrary.Id);
        await using (MediaContext seedContext = new(_options))
        {
            seedContext.LibraryTv.Add(new(_tvLibrary.Id, show.Id));
            await seedContext.SaveChangesAsync();
        }

        await using (MediaContext callerContext = new(_options))
        {
            await using IDbContextTransaction transaction =
                await callerContext.Database.BeginTransactionAsync();

            bool moved = await new ShowRepository(callerContext).EnsureFiledUnderLibraryTypeAsync(
                show.Id,
                "anime"
            );

            moved.Should().BeTrue();
            await transaction.RollbackAsync();
        }

        (await LibraryIdOfAsync(show.Id)).Should().Be(_tvLibrary.Id);
        (await LinksOfAsync(show.Id)).Select(link => link.LibraryId).Should().Equal(_tvLibrary.Id);
    }

    private async Task<Tv> AddShowAsync(int id, Ulid libraryId)
    {
        Tv show = new()
        {
            Id = id,
            Title = $"Show {id}",
            TitleSort = $"Show {id}",
            LibraryId = libraryId,
        };

        await using MediaContext context = new(_options);
        context.Tvs.Add(show);
        await context.SaveChangesAsync();

        return show;
    }

    private async Task<Ulid> LibraryIdOfAsync(int tvId)
    {
        await using MediaContext context = new(_options);
        return await context
            .Tvs.AsNoTracking()
            .Where(tv => tv.Id == tvId)
            .Select(tv => tv.LibraryId)
            .SingleAsync();
    }

    private async Task<List<LibraryTv>> LinksOfAsync(int tvId)
    {
        await using MediaContext context = new(_options);
        return await context
            .LibraryTv.AsNoTracking()
            .Where(link => link.TvId == tvId)
            .ToListAsync();
    }

    private sealed class LinkBeforeFirstWriteInterceptor(Ulid libraryId, int tvId)
        : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            InsertLinkOnFirstWrite(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            InsertLinkOnFirstWrite(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void InsertLinkOnFirstWrite(DbCommand command)
        {
            string sql = command.CommandText.TrimStart();
            bool isWrite =
                sql.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                || sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                || sql.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase);
            if (Fired || !isWrite)
                return;

            Fired = true;
            using DbCommand insert = command.Connection!.CreateCommand();
            insert.Transaction = command.Transaction;
            insert.CommandText =
                "INSERT INTO LibraryTv (LibraryId, TvId, AddedBy) VALUES ($library, $tv, 'file')";
            DbParameter library = insert.CreateParameter();
            library.ParameterName = "$library";
            library.Value = libraryId.ToString();
            insert.Parameters.Add(library);
            DbParameter tv = insert.CreateParameter();
            tv.ParameterName = "$tv";
            tv.Value = tvId;
            insert.Parameters.Add(tv);
            insert.ExecuteNonQuery();
        }
    }
}
