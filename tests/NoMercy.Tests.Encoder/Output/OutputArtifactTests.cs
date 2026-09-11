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

using NoMercy.Encoder.Output;
using NoMercy.NmSystem.Domain;

namespace NoMercy.Tests.Encoder.Output;

public class OutputArtifactTests
{
    [Theory]
    [InlineData("master.m3u8", "application/vnd.apple.mpegurl")]
    [InlineData("stream.mpd", "application/dash+xml")]
    [InlineData("clip.mp4", "video/mp4")]
    [InlineData("movie.mkv", "video/x-matroska")]
    [InlineData("video.webm", "video/webm")]
    [InlineData("segment.ts", "video/mp2t")]
    [InlineData("seg.m4s", "video/iso.segment")]
    [InlineData("audio.m4a", "audio/mp4")]
    [InlineData("track.aac", "audio/aac")]
    [InlineData("track.mp3", "audio/mpeg")]
    [InlineData("audio.ogg", "audio/ogg")]
    [InlineData("track.opus", "audio/opus")]
    [InlineData("audio.flac", "audio/flac")]
    [InlineData("subs.vtt", "text/vtt")]
    [InlineData("subs.srt", "text/plain")]
    [InlineData("subs.ass", "text/plain")]
    [InlineData("subs.ssa", "text/plain")]
    [InlineData("subs.ttml", "application/ttml+xml")]
    [InlineData("thumb.webp", "image/webp")]
    [InlineData("thumb.jpg", "image/jpeg")]
    [InlineData("thumb.jpeg", "image/jpeg")]
    [InlineData("thumb.png", "image/png")]
    [InlineData("meta.json", "application/json")]
    [InlineData("font.ttf", "font/ttf")]
    [InlineData("font.otf", "font/otf")]
    [InlineData("font.woff", "font/woff")]
    [InlineData("font.woff2", "font/woff2")]
    public void MimeFromPath_maps_known_extensions(string fileName, string expectedMime)
    {
        string mime = OutputArtifact.MimeFromPath(fileName);
        Assert.Equal(expectedMime, mime);
    }

    [Theory]
    [InlineData("file.xyz")]
    [InlineData("noextension")]
    [InlineData("archive.tar.gz")]
    [InlineData("data.bin")]
    public void MimeFromPath_unknown_extension_returns_octet_stream(string fileName)
    {
        string mime = OutputArtifact.MimeFromPath(fileName);
        Assert.Equal("application/octet-stream", mime);
    }

    /// <summary>
    /// The three audio extensions a stem can be written as come from the one
    /// stem-format vocabulary, not from literals of this table's own. An
    /// encoder that labels a file one way and a register row that labels it
    /// another is a client being handed something other than what it asked
    /// for - hours after the sweep that wrote it, and only on the devices that
    /// care.
    /// </summary>
    [Fact]
    public void MimeFromPath_uses_the_stem_format_vocabulary()
    {
        Assert.Equal(StemFormats.OggContentType, OutputArtifact.MimeFromPath("audio.ogg"));
        Assert.Equal(
            StemFormats.OpusContentType,
            OutputArtifact.MimeFromPath($"audio.{StemFormats.Opus}")
        );
        Assert.Equal(
            StemFormats.FlacContentType,
            OutputArtifact.MimeFromPath($"audio.{StemFormats.Flac}")
        );
    }

    [Fact]
    public void MimeFromPath_is_case_insensitive()
    {
        Assert.Equal("video/mp4", OutputArtifact.MimeFromPath("CLIP.MP4"));
        Assert.Equal("application/vnd.apple.mpegurl", OutputArtifact.MimeFromPath("MASTER.M3U8"));
        Assert.Equal("text/vtt", OutputArtifact.MimeFromPath("SUBS.VTT"));
    }

    [Fact]
    public void OutputArtifact_construction_round_trips_all_fields()
    {
        OutputArtifact artifact = new(
            Path: "/out/master.m3u8",
            SizeBytes: 4096L,
            Sha256: "deadbeef",
            MediaType: "application/vnd.apple.mpegurl"
        );

        Assert.Equal("/out/master.m3u8", artifact.Path);
        Assert.Equal(4096L, artifact.SizeBytes);
        Assert.Equal("deadbeef", artifact.Sha256);
        Assert.Equal("application/vnd.apple.mpegurl", artifact.MediaType);
    }

    [Fact]
    public void MimeFromPath_handles_path_with_directories()
    {
        // Should only look at the extension, not be confused by dots in directory names
        Assert.Equal("video/mp4", OutputArtifact.MimeFromPath("/some/path.v1/clip.mp4"));
        Assert.Equal(
            "application/vnd.apple.mpegurl",
            OutputArtifact.MimeFromPath("/hls/v720p/master.m3u8")
        );
    }
}
