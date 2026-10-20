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
using Moq;
using NoMercy.MediaProcessing.Lyrics;
using NoMercy.Storage;
using TagLib;
using TagLib.Id3v2;
using TagLib.Ogg;

namespace NoMercy.Tests.MediaProcessing.Lyrics;

/// <summary>
/// Lyrics read from a file's own tags: a FLAC's Vorbis fields and an MP3's
/// USLT and SYLT frames, through the storage facade (#620). Ogg Vorbis shares
/// the Xiph branch with FLAC, and MP4 goes through TagLib's generic lyrics
/// field; neither has a fixture small enough to write here.
/// </summary>
[Trait("Category", "Unit")]
public sealed class EmbeddedLyricsReaderTests : IDisposable
{
    // Minimal valid FLAC: fLaC marker + STREAMINFO (44100 Hz, stereo, 16-bit, 0 samples).
    private static readonly byte[] MinimalFlacBytes = Convert.FromBase64String(
        "ZkxhQ4AAACIQABAAAAAAAAAACsRC8AAAAAAAAAAAAAAAAAAAAAAAAAAA"
    );

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"nm-embedded-lyrics-{Guid.NewGuid():N}"
    );

    public EmbeddedLyricsReaderTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task A_flac_lyrics_field_is_read_as_plain_lines()
    {
        string path = Flac("plain.flac");
        using (TagLib.File file = TagLib.File.Create(path))
        {
            file.Tag.Lyrics = "first line\n\nsecond line\r\n";
            file.Save();
        }

        EmbeddedLyrics? lyrics = await Reader().ReadAsync(path);

        lyrics.Should().NotBeNull();
        lyrics!.Lines.Should().Equal("first line", "second line");
        lyrics.WasSynced.Should().BeFalse();
        lyrics.Language.Should().BeNull();
    }

    [Fact]
    public async Task A_flac_unsyncedlyrics_field_is_read_when_lyrics_is_empty()
    {
        string path = Flac("unsynced.flac");
        using (TagLib.File file = TagLib.File.Create(path))
        {
            XiphComment xiph = (XiphComment)file.GetTag(TagTypes.Xiph, create: true);
            xiph.SetField("UNSYNCEDLYRICS", "only here");
            file.Save();
        }

        EmbeddedLyrics? lyrics = await Reader().ReadAsync(path);

        lyrics!.Lines.Should().Equal("only here");
    }

    [Fact]
    public async Task Lrc_timestamps_in_a_plain_field_count_as_synced_and_are_stripped()
    {
        string path = Flac("lrc.flac");
        using (TagLib.File file = TagLib.File.Create(path))
        {
            file.Tag.Lyrics = "[00:12.30]first\n[01:02.00][01:30.50]twice";
            file.Save();
        }

        EmbeddedLyrics? lyrics = await Reader().ReadAsync(path);

        lyrics!.Lines.Should().Equal("first", "twice");
        lyrics.WasSynced.Should().BeTrue();
    }

    [Fact]
    public async Task An_mp3_uslt_frame_is_read_with_its_language_only_when_two_letters()
    {
        string path = Mp3("uslt.mp3");
        using (TagLib.File file = TagLib.File.Create(path, ReadStyle.None))
        {
            TagLib.Id3v2.Tag id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, create: true);
            UnsynchronisedLyricsFrame frame = UnsynchronisedLyricsFrame.Get(
                id3,
                "",
                "eng",
                create: true
            );
            frame.Text = "hello\nworld";
            file.Save();
        }

        EmbeddedLyrics? lyrics = await Reader().ReadAsync(path);

        lyrics!.Lines.Should().Equal("hello", "world");
        lyrics.WasSynced.Should().BeFalse();
        lyrics.Language.Should().BeNull("ID3 names languages in ISO 639-2, not 639-1");
    }

    [Fact]
    public async Task An_mp3_sylt_frame_wins_over_uslt_and_is_synced()
    {
        string path = Mp3("sylt.mp3");
        using (TagLib.File file = TagLib.File.Create(path, ReadStyle.None))
        {
            TagLib.Id3v2.Tag id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, create: true);
            UnsynchronisedLyricsFrame plain = UnsynchronisedLyricsFrame.Get(
                id3,
                "",
                "eng",
                create: true
            );
            plain.Text = "plain text";
            SynchronisedLyricsFrame synced = new("", "eng", SynchedTextType.Lyrics)
            {
                Format = TimestampFormat.AbsoluteMilliseconds,
                Text = [new SynchedText(1000, "timed one"), new SynchedText(5000, "timed two")],
            };
            id3.AddFrame(synced);
            file.Save();
        }

        EmbeddedLyrics? lyrics = await Reader().ReadAsync(path);

        lyrics!.Lines.Should().Equal("timed one", "timed two");
        lyrics.WasSynced.Should().BeTrue();
    }

    [Fact]
    public async Task A_file_without_lyrics_reads_as_null()
    {
        string path = Flac("none.flac");

        (await Reader().ReadAsync(path)).Should().BeNull();
    }

    [Fact]
    public async Task A_file_that_cannot_be_opened_reads_as_null()
    {
        (await Reader().ReadAsync(Path.Combine(_folder, "missing.flac"))).Should().BeNull();
    }

    private string Flac(string name)
    {
        string path = Path.Combine(_folder, name);
        System.IO.File.WriteAllBytes(path, MinimalFlacBytes);
        return path;
    }

    // Tags only: TagLib reads an ID3v2 tag from an .mp3 that holds no audio
    // frame when the properties are not asked for, which is how the reader
    // opens every file.
    private string Mp3(string name)
    {
        string path = Path.Combine(_folder, name);
        System.IO.File.WriteAllBytes(path, []);
        return path;
    }

    private static EmbeddedLyricsReader Reader()
    {
        Mock<IStorage> storage = new();
        storage
            .Setup(s => s.AcquireLocalPathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, CancellationToken _) => new LocalPathLease(path));
        return new(storage.Object);
    }
}
