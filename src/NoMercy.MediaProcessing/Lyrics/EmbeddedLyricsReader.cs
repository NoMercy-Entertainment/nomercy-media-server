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

using System.Text.RegularExpressions;
using NoMercy.Storage;
using TagLib;
using TagLib.Id3v2;
using TagLib.Ogg;

namespace NoMercy.MediaProcessing.Lyrics;

/// <summary>
/// Reads lyrics out of a music file's own tags with TagLib. The generic
/// <see cref="Tag.Lyrics" /> already maps ID3 USLT, Vorbis LYRICS and MP4
/// lyrics; on top of that, an ID3 SYLT frame is preferred when present because
/// it carries timing, and a Vorbis UNSYNCEDLYRICS field is read when LYRICS is
/// empty, because taggers disagree on which of the two to write.
/// </summary>
public sealed partial class EmbeddedLyricsReader(IStorage storage) : IEmbeddedLyricsReader
{
    public async Task<EmbeddedLyrics?> ReadAsync(string path, CancellationToken ct = default)
    {
        try
        {
            await using LocalPathLease lease = await storage.AcquireLocalPathAsync(path, ct);

            // Tags only: the audio properties are not needed, and reading them
            // would scan for a frame header on a file this never plays.
            using TagLib.File tagFile = TagLib.File.Create(lease.Path, ReadStyle.None);
            return Extract(tagFile);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The lyrics in an opened file, or null when it carries none.</summary>
    internal static EmbeddedLyrics? Extract(TagLib.File tagFile)
    {
        if (tagFile.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
        {
            EmbeddedLyrics? synced = FromSynchronisedFrame(id3);
            if (synced is not null)
                return synced;

            UnsynchronisedLyricsFrame? unsynced = id3.GetFrames<UnsynchronisedLyricsFrame>()
                .FirstOrDefault(frame => !string.IsNullOrWhiteSpace(frame.Text));
            if (unsynced is not null)
                return FromText(unsynced.Text, Iso639Part1(unsynced.Language));
        }

        if (tagFile.GetTag(TagTypes.Xiph) is XiphComment xiph)
        {
            string? text = xiph.GetFirstField("LYRICS") ?? xiph.GetFirstField("UNSYNCEDLYRICS");
            if (!string.IsNullOrWhiteSpace(text))
                return FromText(text, null);
        }

        return string.IsNullOrWhiteSpace(tagFile.Tag?.Lyrics)
            ? null
            : FromText(tagFile.Tag!.Lyrics, null);
    }

    private static EmbeddedLyrics? FromSynchronisedFrame(TagLib.Id3v2.Tag id3)
    {
        SynchronisedLyricsFrame? frame = id3.GetFrames<SynchronisedLyricsFrame>()
            .FirstOrDefault(candidate => candidate.Text.Length > 0);
        if (frame is null)
            return null;

        List<string> lines = frame
            .Text.Select(entry => entry.Text.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        return lines.Count == 0 ? null : new(lines, true, Iso639Part1(frame.Language));
    }

    /// <summary>
    /// Text lines, with LRC timestamps stripped. A tagger that writes LRC into
    /// a plain lyrics field still delivered timing, so that counts as synced.
    /// </summary>
    internal static EmbeddedLyrics? FromText(string text, string? language)
    {
        bool synced = false;
        List<string> lines = [];
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            Match stamp = LrcTimestamp().Match(line);
            if (stamp.Success)
            {
                synced = true;
                line = line[stamp.Length..];
            }

            line = line.Trim();
            if (line.Length > 0)
                lines.Add(line);
        }

        return lines.Count == 0 ? null : new(lines, synced, language);
    }

    /// <summary>
    /// ID3 names a language in ISO 639-2, three letters, and the contract
    /// promises ISO 639-1. Only a code that is already two letters is passed
    /// on; a three-letter one is dropped rather than guessed at.
    /// </summary>
    private static string? Iso639Part1(string? language) =>
        language is { Length: 2 } && language.All(char.IsAsciiLetterLower) ? language : null;

    [GeneratedRegex(@"^(\[\d{1,2}:\d{2}(?:[.:]\d{1,3})?\])+")]
    private static partial Regex LrcTimestamp();
}
