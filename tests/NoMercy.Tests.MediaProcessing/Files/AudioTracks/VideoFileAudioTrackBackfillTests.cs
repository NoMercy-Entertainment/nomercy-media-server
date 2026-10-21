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
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Storage;
using NoMercy.Encoder.Analysis;
using NoMercy.MediaProcessing.Files.AudioTracks;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Tests.MediaProcessing.Files.AudioTracks;

[Trait("Category", "Unit")]
public sealed class VideoFileAudioTrackBackfillTests : IDisposable
{
    private const string Master = """
        #EXTM3U
        #EXT-X-VERSION:6
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio_eac3",LANGUAGE="jpn",AUTOSELECT=YES,DEFAULT=YES,URI="audio_jpn_eac3/audio_jpn_eac3.m3u8",NAME="Japanese"
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio_aac",LANGUAGE="eng",AUTOSELECT=YES,DEFAULT=NO,URI="audio_eng_aac/audio_eng_aac.m3u8",NAME="English"
        #EXT-X-STREAM-INF:BANDWIDTH=5000000,AUDIO="audio_eac3"
        video_1920x1080/video_1920x1080.m3u8
        """;

    private readonly string _root;
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly Ulid _folderId = Ulid.NewUlid();
    private readonly Mock<IMediaAnalyzer> _analyzer = new();
    private readonly VideoFileAudioTrackBackfill _backfill;

    public VideoFileAudioTrackBackfillTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"nm-audio-tracks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _connection = new("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;
        using (MediaContext context = new(_options))
        {
            context.Database.EnsureCreated();
            context.Folders.Add(
                new()
                {
                    Id = _folderId,
                    Path = "/media",
                    DriverId = Driver.SystemLocalDriverId,
                    Driver = new()
                    {
                        Id = Driver.SystemLocalDriverId,
                        Name = "local",
                        Type = "local",
                    },
                }
            );
            context.SaveChanges();
        }

        LocalStorageDriver driver = new();
        IStorage storage = new LocalStorage(driver, new StoragePathGuard([_root], driver));
        Mock<IStorageFactory> storageFactory = new();
        storageFactory
            .Setup(factory => factory.For(_folderId, Driver.SystemLocalDriverId, string.Empty))
            .Returns(storage);

        Mock<IDbContextFactory<MediaContext>> contextFactory = new();
        contextFactory
            .Setup(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MediaContext(_options));

        _analyzer
            .Setup(analyzer =>
                analyzer.AnalyzeAsync(
                    It.Is<string>(path => path.Contains("audio_jpn_eac3")),
                    It.IsAny<IStorage>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                Info(Stream("eac3", 2, "stereo", 48000, 224, "Dolby Digital Plus + Dolby Atmos"))
            );
        _analyzer
            .Setup(analyzer =>
                analyzer.AnalyzeAsync(
                    It.Is<string>(path => path.Contains("audio_eng_aac")),
                    It.IsAny<IStorage>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(Info(Stream("aac", 6, "5.1", 44100, 384, "LC")));

        _backfill = new(contextFactory.Object, storageFactory.Object, _analyzer.Object);
    }

    public void Dispose()
    {
        _connection.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked handle on Windows must not fail the run.
        }
        GC.SuppressFinalize(this);
    }

    private static AudioStreamInfo Stream(
        string codec,
        int channels,
        string layout,
        int sampleRate,
        long kbps,
        string profile
    ) =>
        new(
            Index: 0,
            Codec: codec,
            Channels: channels,
            SampleRate: sampleRate,
            BitRateKbps: kbps,
            Language: null,
            IsDefault: false,
            IsForced: false,
            Profile: profile,
            ChannelLayout: layout
        );

    private static MediaInfo Info(AudioStreamInfo audio) =>
        new(
            FilePath: "segment",
            Format: "mpegts",
            Duration: TimeSpan.FromSeconds(6),
            OverallBitRateKbps: 0,
            FileSizeBytes: 0,
            VideoStreams: [],
            AudioStreams: [audio],
            SubtitleStreams: [],
            Chapters: []
        );

    private string WriteRelease(string name)
    {
        string folder = Path.Combine(_root, name).Replace('\\', '/');
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ".m3u8"), Master);
        foreach (string rendition in new[] { "audio_jpn_eac3", "audio_eng_aac" })
        {
            string dir = Path.Combine(folder, rendition);
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, rendition + ".m3u8"),
                $"#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6.0,\n{rendition}_000.ts\n#EXT-X-ENDLIST\n"
            );
            File.WriteAllBytes(Path.Combine(dir, rendition + "_000.ts"), [0]);
        }
        return folder;
    }

    private VideoFile AddVideoFile(string name)
    {
        VideoFile file = new()
        {
            Filename = $"/{name}.m3u8",
            HostFolder = WriteRelease(name),
            Share = _folderId.ToString(),
        };
        using MediaContext context = new(_options);
        context.VideoFiles.Add(file);
        context.SaveChanges();
        return file;
    }

    private List<VideoFileAudioTrack> TracksOf(Ulid videoFileId)
    {
        using MediaContext context = new(_options);
        return context
            .VideoFileAudioTracks.AsNoTracking()
            .Where(track => track.VideoFileId == videoFileId)
            .OrderBy(track => track.Language)
            .ToList();
    }

    [Fact]
    public async Task RunAsync_FileWithTwoAudioRenditions_StoresCodecChannelsAndLayoutPerTrack()
    {
        VideoFile file = AddVideoFile("Example.One");

        int filled = await _backfill.RunAsync();

        filled.Should().Be(1);
        List<VideoFileAudioTrack> tracks = TracksOf(file.Id);
        tracks.Should().HaveCount(2);

        VideoFileAudioTrack english = tracks[0];
        english.Language.Should().Be("eng");
        english.Codec.Should().Be("aac");
        english.Channels.Should().Be(6);
        english.ChannelLayout.Should().Be("5.1");
        english.SampleRate.Should().Be(44100);
        english.BitRateKbps.Should().Be(384);
        english.IsDefault.Should().BeFalse();
        english.GroupId.Should().Be("audio_aac");
        english.Uri.Should().Be("audio_eng_aac/audio_eng_aac.m3u8");

        VideoFileAudioTrack japanese = tracks[1];
        japanese.Language.Should().Be("jpn");
        japanese.Codec.Should().Be("eac3");
        japanese.Profile.Should().Be("Dolby Digital Plus + Dolby Atmos");
        japanese.Channels.Should().Be(2);
        japanese.ChannelLayout.Should().Be("stereo");
        japanese.IsDefault.Should().BeTrue();
        japanese.GroupId.Should().Be("audio_eac3");
    }

    [Fact]
    public async Task RunAsync_FillsFilesWithoutRowsAndLeavesExistingRowsUnchanged()
    {
        VideoFile withRows = AddVideoFile("Example.Two");
        VideoFile withoutRows = AddVideoFile("Example.Three");
        VideoFileAudioTrack existing = new()
        {
            VideoFileId = withRows.Id,
            Codec = "opus",
            Channels = 8,
            Language = "deu",
        };
        using (MediaContext context = new(_options))
        {
            context.VideoFileAudioTracks.Add(existing);
            context.SaveChanges();
        }

        int filled = await _backfill.RunAsync();

        filled.Should().Be(1);
        TracksOf(withoutRows.Id).Should().HaveCount(2);
        List<VideoFileAudioTrack> kept = TracksOf(withRows.Id);
        kept.Should().ContainSingle();
        kept[0].Id.Should().Be(existing.Id);
        kept[0].Codec.Should().Be("opus");
        kept[0].Channels.Should().Be(8);

        (await _backfill.RunAsync()).Should().Be(0);
    }

    // A direct-play file is the whole video. Reading it as playlist text loaded gigabytes per
    // file into memory on every scan (2026-10-21: the dev server reached 14.6 GB and crashed).
    [Fact]
    public async Task RunAsync_FileThatIsNotAnHlsMaster_IsNeverReadAsAPlaylist()
    {
        string folder = WriteRelease("Example.Four");
        // Same bytes as a master, so a read would show up as stored tracks.
        File.Copy(
            Path.Combine(folder, "Example.Four.m3u8"),
            Path.Combine(folder, "Example.Four.mp4")
        );
        VideoFile direct = new()
        {
            Filename = "/Example.Four.mp4",
            HostFolder = folder,
            Share = _folderId.ToString(),
        };
        using (MediaContext context = new(_options))
        {
            context.VideoFiles.Add(direct);
            context.SaveChanges();
        }

        int filled = await _backfill.RunAsync();

        filled.Should().Be(0);
        TracksOf(direct.Id).Should().BeEmpty();
        _analyzer.Verify(
            analyzer =>
                analyzer.AnalyzeAsync(
                    It.IsAny<string>(),
                    It.IsAny<IStorage>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }
}
