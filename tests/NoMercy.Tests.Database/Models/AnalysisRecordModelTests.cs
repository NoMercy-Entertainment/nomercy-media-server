using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.Storage;

namespace NoMercy.Tests.Database.Models;

/// <summary>
/// The DJ record and the stem register hang off a track and off the derived
/// store: deleting either parent must take the rows with it, and the JSON
/// columns must come back byte-for-byte.
/// </summary>
public class AnalysisRecordModelTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<MediaContext> _options;

    public AnalysisRecordModelTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;
        using MediaContext context = new(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    // Track.FolderId is a required FK to Folder, which is itself a required FK
    // to Driver, both enforced at the SQLite level (SqliteConnection enforces
    // foreign keys by default). A dangling FolderId — as a bare scalar would
    // leave it — fails the very first insert with "FOREIGN KEY constraint
    // failed" before any of this file's own tables are touched, so TrackRow
    // carries the whole chain through navigation properties for EF to insert
    // alongside the track.
    private static Track TrackRow(Guid id)
    {
        Ulid folderId = Ulid.NewUlid();
        return new Track
        {
            Id = id,
            Name = "A Track",
            Duration = "03:45",
            FolderId = folderId,
            LibraryFolder = new Folder
            {
                Id = folderId,
                Path = "/music",
                DriverId = Driver.SystemLocalDriverId,
                Driver = new Driver
                {
                    Id = Driver.SystemLocalDriverId,
                    Name = "local",
                    Type = "local",
                },
            },
        };
    }

    [Fact]
    public async Task DeletingATrack_RemovesItsDjAnalysisAndStems()
    {
        Guid trackId = Guid.NewGuid();
        await using (MediaContext context = new(_options))
        {
            context.Tracks.Add(TrackRow(trackId));
            context.DerivedAudio.Add(
                new DerivedAudio
                {
                    Key = new string('a', 64),
                    ContentType = "audio/opus",
                    Bytes = 10,
                    CreatedAt = DateTime.UtcNow,
                    LastUsedAt = DateTime.UtcNow,
                }
            );
            context.TrackDjAnalysis.Add(
                new TrackDjAnalysis
                {
                    TrackId = trackId,
                    ProducerPluginId = Ulid.NewUlid(),
                    DjAnalyzerVersion = 1,
                    BaseAnalyzerVersion = 4,
                    State = AudioAnalysisState.Ok,
                    PhraseStartsMs = "[0,15000]",
                    AnalyzedAt = DateTime.UtcNow,
                }
            );
            context.TrackStems.Add(
                new TrackStem
                {
                    Id = Ulid.NewUlid(),
                    TrackId = trackId,
                    Kind = "vocals",
                    Coverage = StemCoverage.Full,
                    Format = "opus",
                    SampleRate = 48000,
                    StorageKey = new string('a', 64),
                    ProducerVersion = "spleeter-2stems-f16@v1.0.41",
                    CreatedAt = DateTime.UtcNow,
                }
            );
            await context.SaveChangesAsync();
        }

        await using (MediaContext context = new(_options))
        {
            await context.Tracks.Where(track => track.Id == trackId).ExecuteDeleteAsync();
        }

        await using MediaContext read = new(_options);
        Assert.Empty(await read.TrackDjAnalysis.AsNoTracking().ToListAsync());
        Assert.Empty(await read.TrackStems.AsNoTracking().ToListAsync());
        Assert.Single(await read.DerivedAudio.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task DeletingADerivedAudioRow_RemovesTheStemsThatPointAtIt()
    {
        Guid trackId = Guid.NewGuid();
        string key = new string('b', 64);
        await using (MediaContext context = new(_options))
        {
            context.Tracks.Add(TrackRow(trackId));
            context.DerivedAudio.Add(
                new DerivedAudio
                {
                    Key = key,
                    ContentType = "audio/opus",
                    Bytes = 10,
                    CreatedAt = DateTime.UtcNow,
                    LastUsedAt = DateTime.UtcNow,
                }
            );
            context.TrackStems.Add(
                new TrackStem
                {
                    Id = Ulid.NewUlid(),
                    TrackId = trackId,
                    Kind = "vocals",
                    Coverage = StemCoverage.MixIn,
                    WindowStartMs = 0,
                    WindowEndMs = 45000,
                    Format = "opus",
                    SampleRate = 48000,
                    StorageKey = key,
                    ProducerVersion = "spleeter-2stems-f16@v1.0.41",
                    CreatedAt = DateTime.UtcNow,
                }
            );
            await context.SaveChangesAsync();
        }

        await using (MediaContext context = new(_options))
        {
            await context.DerivedAudio.Where(row => row.Key == key).ExecuteDeleteAsync();
        }

        await using MediaContext read = new(_options);
        Assert.Empty(await read.TrackStems.AsNoTracking().ToListAsync());
        Assert.Single(await read.Tracks.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task TheSameStemTwice_IsRejectedByTheUniqueIndex()
    {
        Guid trackId = Guid.NewGuid();
        string key = new string('c', 64);
        await using MediaContext context = new(_options);
        context.Tracks.Add(TrackRow(trackId));
        context.DerivedAudio.Add(
            new DerivedAudio
            {
                Key = key,
                ContentType = "audio/opus",
                Bytes = 10,
                CreatedAt = DateTime.UtcNow,
                LastUsedAt = DateTime.UtcNow,
            }
        );
        TrackStem Stem() =>
            new()
            {
                Id = Ulid.NewUlid(),
                TrackId = trackId,
                Kind = "vocals",
                Coverage = StemCoverage.Full,
                Format = "opus",
                SampleRate = 48000,
                StorageKey = key,
                ProducerVersion = "p@1",
                CreatedAt = DateTime.UtcNow,
            };
        context.TrackStems.Add(Stem());
        context.TrackStems.Add(Stem());

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }
}
