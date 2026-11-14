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

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Encoder.Orchestration;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Strategies;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Encoder.Output;

public sealed class EncodeOutputValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nm-validate-{Guid.NewGuid():N}"
    );

    public EncodeOutputValidatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private void Write(string rel, string text)
    {
        string path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void WriteBytes(string rel, int length)
    {
        string path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
    }

    private const string Media =
        "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:6\n#EXTINF:6.0,\nseg0.ts\n#EXTINF:6.0,\nseg1.ts\n#EXT-X-ENDLIST\n";

    [Fact]
    public void CompleteHlsOutput_HasNoProblems()
    {
        Write("video_1080p/video.m3u8", Media);
        WriteBytes("video_1080p/seg0.ts", 100);
        WriteBytes("video_1080p/seg1.ts", 100);
        Write("master.m3u8", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nvideo_1080p/video.m3u8\n");

        EncodeOutputValidator.Validate(_root).Should().BeEmpty();
    }

    [Fact]
    public void PlaylistNamingAMissingSegment_IsReported()
    {
        Write("video_1080p/video.m3u8", Media);
        WriteBytes("video_1080p/seg0.ts", 100);

        EncodeOutputValidator.Validate(_root).Should().ContainSingle(p => p.Contains("seg1.ts"));
    }

    [Fact]
    public void ZeroByteSegment_IsReported()
    {
        Write("video_1080p/video.m3u8", Media);
        WriteBytes("video_1080p/seg0.ts", 100);
        WriteBytes("video_1080p/seg1.ts", 0);

        EncodeOutputValidator.Validate(_root).Should().ContainSingle(p => p.Contains("seg1.ts"));
    }

    [Fact]
    public void MediaPlaylistWithoutEndList_IsReported()
    {
        Write("video_1080p/video.m3u8", Media.Replace("#EXT-X-ENDLIST\n", ""));
        WriteBytes("video_1080p/seg0.ts", 100);
        WriteBytes("video_1080p/seg1.ts", 100);

        EncodeOutputValidator.Validate(_root).Should().ContainSingle(p => p.Contains("ENDLIST"));
    }

    [Fact]
    public void EmptyPlaylist_IsReported()
    {
        Write("video_1080p/video.m3u8", "");

        EncodeOutputValidator.Validate(_root).Should().NotBeEmpty();
    }

    [Fact]
    public void MasterNamingAMissingPlaylist_IsReported()
    {
        Write("master.m3u8", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nvideo_1080p/video.m3u8\n");

        EncodeOutputValidator.Validate(_root).Should().ContainSingle(p => p.Contains("video.m3u8"));
    }

    [Fact]
    public void ZeroByteMediaFile_IsReported()
    {
        WriteBytes("audio.mp4", 0);

        EncodeOutputValidator.Validate(_root).Should().ContainSingle(p => p.Contains("audio.mp4"));
    }

    [Fact]
    public void TruncatedVideoRendition_IsReported()
    {
        Write("video_1920x1080_SDR/video_1920x1080_SDR.m3u8", Media);
        Write("video_1920x1080_SDR/seg0.ts", "x");
        Write("video_1920x1080_SDR/seg1.ts", "x");

        IReadOnlyList<string> problems = EncodeOutputValidator.ValidateDurations(_root, 7450);

        Assert.Contains(problems, p => p.Contains("video_1920x1080_SDR") && p.Contains("12"));
    }

    [Fact]
    public void TruncationMessage_UsesTheInvariantCulture()
    {
        Write("video_a/video_a.m3u8", Media);
        System.Globalization.CultureInfo previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new("nl-NL");
        try
        {
            IReadOnlyList<string> problems = EncodeOutputValidator.ValidateDurations(_root, 7450);

            problems
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("lasts 12.0 s, the source lasts 7450.0 s");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void FullLengthRenditionsAndSubtitles_HaveNoDurationProblems()
    {
        Write("video_a/video_a.m3u8", Media);
        Write("audio_eng_aac/audio_eng_aac.m3u8", Media);
        Write("subtitles/sub_eng.m3u8", Media);

        Assert.Empty(EncodeOutputValidator.ValidateDurations(_root, 12.3));
    }

    [Fact]
    public async Task Publish_OfCorruptOutput_ThrowsAndMovesNothing()
    {
        string tempDir = Path.Combine(_root, "cache");
        string library = Path.Combine(_root, "library");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "video.m3u8"), Media);
        File.WriteAllBytes(Path.Combine(tempDir, "seg0.ts"), new byte[100]);

        IStorage storage = new LocalStorage(
            new LocalStorageDriver(),
            new([], new LocalStorageDriver())
        );
        EncodingOrchestrator orchestrator = new(
            new Mock<IStrategyResolver>().Object,
            storage,
            new Mock<IEncoder>().Object,
            NullLogger<EncodingOrchestrator>.Instance
        );

        Func<Task> publish = () =>
            orchestrator.PublishTempDirAsync(tempDir, library, storage, null, default);

        await publish.Should().ThrowAsync<InvalidDataException>();
        Directory.Exists(library).Should().BeFalse();
    }
}
