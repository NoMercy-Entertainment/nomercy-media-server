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

using System.Text;
using Moq;
using NoMercy.NmSystem.FFProbe;
using NoMercy.Storage;

namespace NoMercy.Tests.NmSystem.FFProbe;

/// <summary>
/// A probe that did not happen must not look like a file with no streams and
/// no duration. A cancelled scan stops; a failed probe says so (#320).
/// </summary>
[Trait("Category", "Unit")]
public class FfProbeServiceFailureTests
{
    private static CancellationToken Cancelled => new(canceled: true);

    [Fact]
    public async Task A_cancelled_local_probe_is_cancelled_not_an_empty_result()
    {
        Func<Task> probe = () => new FfProbeService().CreateAsync("Movie.mkv", Cancelled);

        await probe.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_cancelled_remote_probe_is_cancelled_not_an_empty_result()
    {
        Func<Task> probe = () =>
            new FfProbeService().CreateAsync(RemoteDriver(), "Movie.mkv", Cancelled);

        await probe.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_cancelled_playlist_read_is_cancelled_not_an_empty_result()
    {
        Func<Task> probe = () =>
            new FfProbeService().CreateAsync(RemoteDriver(), "Show/master.m3u8", Cancelled);

        await probe.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_probe_of_a_file_that_is_not_there_says_it_failed()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"nm-missing-{Guid.NewGuid():N}.mkv");

        FfProbeData result = await new FfProbeService().CreateAsync(missing);

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void A_probe_without_errors_has_not_failed()
    {
        new FfProbeData().Failed.Should().BeFalse();
    }

    [Fact]
    public void An_answer_with_no_format_and_no_streams_says_the_probe_failed()
    {
        // ffprobe prints "{}" and exits 1 for a file it cannot open: missing,
        // empty, or not media. The exit code is never read, so this answer
        // used to pass as a real probe of a file with no streams.
        FfProbeData result = new FfProbeService().FromFfprobeJson("Movie.mkv", "{\n\n}");

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void An_empty_answer_says_the_probe_failed()
    {
        FfProbeData result = new FfProbeService().FromFfprobeJson("Movie.mkv", string.Empty);

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void An_answer_with_a_format_has_not_failed()
    {
        const string json = """
            {
              "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080 }
              ],
              "format": { "filename": "Movie.mkv", "duration": "61.5" }
            }
            """;

        FfProbeData result = new FfProbeService().FromFfprobeJson("Movie.mkv", json);

        result.Failed.Should().BeFalse();
        result.Duration.Should().Be(TimeSpan.FromSeconds(61.5));
        result.VideoStreams.Should().ContainSingle();
    }

    [Fact]
    public void An_empty_probe_answer_asks_for_one_retry_with_the_mp3_demuxer()
    {
        // An MP3 stream in a RIFF/WAVE container with a leading ID3v2 tag makes
        // the wav demuxer fail and ffprobe print "{}".
        FfProbeService.NeedsForcedMp3Retry("{\n\n}").Should().BeTrue();
    }

    [Fact]
    public void A_probe_answer_with_a_format_needs_no_retry()
    {
        const string json = """{ "format": { "filename": "01 Track.mp3", "duration": "61.5" } }""";

        FfProbeService.NeedsForcedMp3Retry(json).Should().BeFalse();
    }

    [Fact]
    public void A_timed_out_probe_with_no_output_needs_no_mp3_retry()
    {
        FfProbeService.NeedsForcedMp3Retry(string.Empty).Should().BeFalse();
    }

    [Fact]
    public void The_forced_probe_puts_the_input_format_before_the_file()
    {
        string args = FfProbeService.BuildProbeArguments(
            "/music/Artist/[2000] Album/01 Track.mp3",
            "mp3"
        );

        args.Should()
            .Be(
                "-hide_banner -v quiet -f mp3 -show_format -show_streams -print_format json \"/music/Artist/[2000] Album/01 Track.mp3\""
            );
    }

    [Fact]
    public void The_normal_probe_forces_no_input_format()
    {
        string args = FfProbeService.BuildProbeArguments("/music/Artist/[2000] Album/01 Track.mp3");

        args.Should().NotContain("-f ");
    }

    private static IStorageDriver RemoteDriver()
    {
        Mock<IStorageDriver> driver = new();
        driver
            .Setup(d => d.OpenRead(It.IsAny<string>()))
            .Returns(() => new MemoryStream(Encoding.UTF8.GetBytes("#EXTM3U\n")));

        return driver.Object;
    }
}
