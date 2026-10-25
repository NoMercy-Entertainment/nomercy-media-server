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

using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using NoMercy.DiscFormat.Disc.Bdmv;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.NmSystem.Dto;
using NoMercy.OpticalMedia.Drives;
using NoMercy.Storage;

namespace NoMercy.OpticalMedia.Sources.Bluray;

/// <summary>
/// Bluray disc reader. Uses nomercy-ffmpeg's <c>bluray:</c> protocol via
/// libbluray. <see cref="ProbeAsync"/> enumerates every usable playlist
/// the disc exposes by parsing libbluray's stderr playlist dump (cheap,
/// one ffprobe call). <see cref="ProbeTitleAsync"/> returns the detailed
/// streams + chapters for one playlist (slower, one ffprobe per call).
///
/// <see cref="ProbeAsync"/> also reads the disc-embedded title from
/// <c>BDMV/META/DL/bdmt_*.xml</c> (if present) and surfaces it in
/// <see cref="DiscInfo.DiscTitle"/>, which downstream identification
/// prefers over the raw volume label.
/// </summary>
public sealed partial class BlurayDiscSource(
    EncoderOptions options,
    IProcessRunner processRunner,
    IStorageDriver storageDriver,
    ILogger<BlurayDiscSource> logger
) : IDiscSource
{
    public OpticalDiscType Type => OpticalDiscType.BluRay;

    public async Task<DiscInfo> ProbeAsync(DiscDrive drive, CancellationToken ct)
    {
        string drivePath = ToBlurayUrl(drive.Path);

        // libbluray dumps the full playlist set on stderr at -v info before
        // it commits to one. Run a thin probe just to capture that dump.
        ProcessResult result = await processRunner.RunAsync(
            options.FfprobePath,
            ["-hide_banner", "-v", "info", "-i", drivePath],
            workingDirectory: null,
            cancellationToken: ct
        );

        // Classify protection state from the stderr — even when the disc is
        // AACS-locked we may still have enumerable playlists (libbluray
        // reads BDMV structure without keys), so we attach Protection to
        // the DiscInfo rather than throwing.
        DiscProtection? protection = ClassifyProtection(result.StdErr);

        // Read the disc-embedded title from bdmt_*.xml before evaluating
        // playlists — we want to populate DiscTitle regardless of whether
        // any playlists are found.
        string? embeddedTitle = TryReadBdmtTitle(drive.Path);

        // ffprobe always exits non-zero here (no input format chosen) — the
        // stderr is the payload we want regardless.
        List<(int Index, TimeSpan Duration)> playlists = ParsePlaylists(result.StdErr);
        if (playlists.Count == 0)
        {
            // Loud warning at INFO level so we always see it in the log when
            // a probe came back empty — separate from the per-message format
            // so existing log-grep filters don't swallow it.
            logger.LogInformation(
                "Bluray probe parsed 0 playlists for {Drive} | exit={Exit} stdout_len={StdOutLen} stderr_len={StdErrLen} stderr_head={StdErrHead}",
                drive.Path,
                result.ExitCode,
                result.StdOut.Length,
                result.StdErr.Length,
                (result.StdErr ?? "").Length > 600
                    ? result.StdErr![..600]
                    : (result.StdErr ?? "(no stderr)")
            );
            return new(
                OpticalDiscType.BluRay,
                drive.Label,
                [],
                null,
                TimeSpan.Zero,
                protection,
                DiscTitle: embeddedTitle
            );
        }

        // Largest playlist by runtime is typically the disc's main feature
        // (movie disc) or the season-concat title (TV disc). Mark it so the
        // UI can highlight it as the default selection.
        TimeSpan maxDuration = playlists.Max(p => p.Duration);
        DiscTitle[] titles =
        [
            .. playlists
                .Select(p => new DiscTitle(
                    Index: p.Index,
                    Name: $"Playlist {p.Index:D5}",
                    Duration: p.Duration,
                    VideoStreams: [],
                    AudioStreams: [],
                    Subtitles: [],
                    Chapters: [],
                    EstimatedSizeBytes: 0,
                    IsMainFeature: p.Duration == maxDuration
                ))
                .OrderByDescending(t => t.Duration),
        ];

        return new(
            Type: OpticalDiscType.BluRay,
            DiscLabel: drive.Label,
            Titles: titles,
            AudioTracks: null,
            TotalDuration: titles.Sum(t => t.Duration.Ticks) is long ticks
                ? TimeSpan.FromTicks(ticks)
                : TimeSpan.Zero,
            Protection: protection,
            DiscTitle: embeddedTitle
        );
    }

    /// <summary>
    /// Inspects libaacs / libbdplus stderr for the well-known "I can't
    /// decrypt this disc" patterns. Returns null when nothing protection-y
    /// is in the output (fully readable or non-protected disc).
    /// </summary>
    internal static DiscProtection? ClassifyProtection(string stderr)
    {
        if (string.IsNullOrEmpty(stderr))
            return null;

        // libaacs SCSI MMC handshake fail (drive can't do AACS bus key)
        if (
            stderr.Contains(
                "Drive does not support reading drive certificate",
                StringComparison.OrdinalIgnoreCase
            )
            || stderr.Contains(
                "Unable to read drive certificate",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return new(
                Kind: "AACS",
                VolumeId: null,
                Message: "Disc is AACS-protected and the optical drive can't establish "
                    + "the bus key required to read encrypted units. The drive's firmware "
                    + "likely lacks AACS-MKI-1 SCSI MMC support."
            );
        }

        // libaacs unable to derive VUK from KEYDB (no matching entry, or
        // entry present but key wrong)
        if (
            stderr.Contains("Unable to decrypt unit (AACS)", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("no matching certificate", StringComparison.OrdinalIgnoreCase)
        )
        {
            return new(
                Kind: "AACS",
                VolumeId: null,
                Message: "Disc is AACS-protected but no matching key was found in KEYDB.cfg."
            );
        }

        // libbdplus
        if (stderr.Contains("no matching converter", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                Kind: "BD+",
                VolumeId: null,
                Message: "Disc uses BD+ and the converter database has no entry for it."
            );
        }

        return null;
    }

    public async Task<DiscTitle> ProbeTitleAsync(
        DiscDrive drive,
        int titleIndex,
        CancellationToken ct
    )
    {
        string url = ToBlurayUrl(drive.Path);
        DiscInfo info = await ScanWithPlaylistAsync(url, titleIndex, ct);
        DiscTitle? single = info.Titles.FirstOrDefault();
        if (single is null)
            return new(
                Index: titleIndex,
                Name: $"Playlist {titleIndex:D5}",
                Duration: TimeSpan.Zero,
                VideoStreams: [],
                AudioStreams: [],
                Subtitles: [],
                Chapters: [],
                EstimatedSizeBytes: 0,
                IsMainFeature: false
            );

        DiscTitle result = single with { Index = titleIndex };
        result = await StampIsMainFeatureAsync(result, drive, ct);
        return ApplyMplsLanguages(result, drive.Path, titleIndex);
    }

    /// <summary>
    /// A single-playlist ffprobe response has no visibility into sibling
    /// playlists, so <see cref="DiscScanner.Parse"/> can't answer "is this
    /// the main feature" on its own — that needs a disc-wide duration
    /// ranking. Reuses <see cref="ProbeAsync"/>, which is already a cheap,
    /// thin <c>-v info</c> stderr-dump probe (no per-title stream re-parse),
    /// to look up this title's rank instead of hardcoding a flag or
    /// re-reading every title's full metadata.
    /// </summary>
    private async Task<DiscTitle> StampIsMainFeatureAsync(
        DiscTitle title,
        DiscDrive drive,
        CancellationToken ct
    )
    {
        DiscInfo discInfo = await ProbeAsync(drive, ct);
        DiscTitle? match = discInfo.Titles.FirstOrDefault(t => t.Index == title.Index);
        return match is null ? title : title with { IsMainFeature = match.IsMainFeature };
    }

    /// <summary>
    /// ffprobe's bluray: protocol never populates stream language tags — that
    /// mapping lives in the playlist's own STN table (BDMV/PLAYLIST/NNNNN.mpls),
    /// not in the elementary stream headers ffprobe reads. The playlist's index
    /// IS the mpls filename number (see <see cref="ParsePlaylists"/>, which reads
    /// it from the same "playlist NNNNN.mpls" libbluray dump), so title index and
    /// mpls file number always match. Audio/subtitle streams are language-matched
    /// by position: the STN table order is what ffprobe's own stream order is
    /// derived from for a bluray: playlist read.
    /// </summary>
    private DiscTitle ApplyMplsLanguages(DiscTitle title, string drivePath, int titleIndex)
    {
        if (title.AudioStreams.Length == 0 && title.Subtitles.Length == 0)
            return title;

        try
        {
            string trimmed = drivePath.TrimEnd('\\', '/');
            string mplsPath = Path.Combine(trimmed, "BDMV", "PLAYLIST", $"{titleIndex:D5}.mpls");

            if (!storageDriver.FileExists(mplsPath))
                return title;

            using Stream stream = storageDriver.OpenRead(mplsPath);
            using MemoryStream buffer = new();
            stream.CopyTo(buffer);
            MplsPlaylist playlist = MplsParser.Parse(buffer.ToArray());

            AudioStreamInfo[] audioStreams = MergeAudioLanguages(
                title.AudioStreams,
                playlist.AudioStreams
            );
            SubtitleStreamInfo[] subtitles = MergeLanguages(
                title.Subtitles,
                playlist.SubtitleStreams,
                (subtitle, language) => subtitle with { Language = language }
            );

            return title with
            {
                AudioStreams = audioStreams,
                Subtitles = subtitles,
            };
        }
        catch (Exception ex)
        {
            // Language enrichment is best-effort — a malformed or unreadable
            // mpls must never fail the title probe itself.
            logger.LogInformation(
                ex,
                "Could not read mpls languages for {Drive} title {Title}: {Message}",
                drivePath,
                titleIndex,
                ex.Message
            );
            return title;
        }
    }

    private static TStream[] MergeLanguages<TStream>(
        TStream[] streams,
        IReadOnlyList<MplsStream> mplsStreams,
        Func<TStream, string?, TStream> withLanguage
    )
    {
        if (streams.Length != mplsStreams.Count)
            return streams;

        TStream[] merged = new TStream[streams.Length];
        for (int i = 0; i < streams.Length; i++)
        {
            merged[i] = withLanguage(streams[i], mplsStreams[i].Language);
        }

        return merged;
    }

    /// <summary>
    /// Audio needs its own merge: a TrueHD track is one entry in the mpls STN
    /// table, but ffprobe demuxes it as two elementary streams back to back —
    /// the TrueHD extension and its embedded AC3 "compatible core" — so a
    /// disc with N logical audio tracks, one of them TrueHD, reports N+1
    /// ffprobe streams. Every other coding type is a 1:1 stream, so a TrueHD
    /// stream immediately followed by an AC3 stream consumes one mpls slot
    /// for the pair; everything else advances the mpls index normally.
    /// </summary>
    private static AudioStreamInfo[] MergeAudioLanguages(
        AudioStreamInfo[] streams,
        IReadOnlyList<MplsStream> mplsStreams
    )
    {
        if (streams.Length == 0)
            return streams;

        AudioStreamInfo[] merged = new AudioStreamInfo[streams.Length];
        int mplsIndex = 0;
        for (int i = 0; i < streams.Length; i++)
        {
            string? language =
                mplsIndex < mplsStreams.Count ? mplsStreams[mplsIndex].Language : null;
            merged[i] = streams[i] with { Language = language };

            bool isTrueHdCorePair =
                streams[i].Codec == "truehd"
                && i + 1 < streams.Length
                && streams[i + 1].Codec == "ac3";
            if (isTrueHdCorePair)
            {
                i++;
                merged[i] = streams[i] with { Language = language };
            }

            mplsIndex++;
        }

        return merged;
    }

    private async Task<DiscInfo> ScanWithPlaylistAsync(
        string drivePath,
        int playlistIndex,
        CancellationToken ct
    )
    {
        ProcessResult result = await processRunner.RunAsync(
            options.FfprobePath,
            [
                "-v",
                "quiet",
                "-playlist",
                playlistIndex.ToString(CultureInfo.InvariantCulture),
                "-print_format",
                "json",
                "-show_format",
                "-show_streams",
                "-show_chapters",
                "-i",
                drivePath,
            ],
            workingDirectory: null,
            cancellationToken: ct
        );

        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.StdOut))
        {
            logger.LogWarning(
                "Per-playlist probe failed for {Drive} #{Playlist} (exit {Exit}): {Stderr}",
                drivePath,
                playlistIndex,
                result.ExitCode,
                TrimStderr(result.StdErr)
            );
            return new(OpticalDiscType.BluRay, null, [], null, TimeSpan.Zero);
        }

        try
        {
            return DiscScanner.Parse(result.StdOut, OpticalDiscType.BluRay);
        }
        catch (InvalidOperationException ex)
        {
            // DiscScanner.Parse wraps a JSON parse failure into
            // InvalidOperationException — a truncated/malformed ffprobe
            // response on a real disc must degrade to an empty probe result
            // here, the same way the !result.IsSuccess branch above does,
            // rather than crash the per-playlist detail fetch.
            logger.LogWarning(
                ex,
                "Per-playlist probe returned unparsable JSON for {Drive} #{Playlist}",
                drivePath,
                playlistIndex
            );
            return new(OpticalDiscType.BluRay, null, [], null, TimeSpan.Zero);
        }
    }

    internal static List<(int Index, TimeSpan Duration)> ParsePlaylists(string stderr)
    {
        List<(int, TimeSpan)> playlists = [];
        if (string.IsNullOrEmpty(stderr))
            return playlists;

        foreach (Match match in PlaylistRegex().Matches(stderr))
        {
            string indexText = match.Groups["index"].Value;
            string durText = match.Groups["duration"].Value;

            if (
                !int.TryParse(
                    indexText,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int idx
                )
            )
                continue;
            if (!TryParseHmsDuration(durText, out TimeSpan dur))
                continue;

            playlists.Add((idx, dur));
        }

        return [.. playlists.DistinctBy(p => p.Item1)];
    }

    private static bool TryParseHmsDuration(string value, out TimeSpan dur)
    {
        dur = TimeSpan.Zero;
        string[] parts = value.Split(':');
        if (parts.Length != 3)
            return false;
        if (
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)
            || !int.TryParse(
                parts[1],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int m
            )
            || !int.TryParse(
                parts[2],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int s
            )
        )
            return false;
        dur = new(h, m, s);
        return true;
    }

    /// <summary>
    /// Attempts to read the human-readable disc title from Blu-ray
    /// <c>BDMV/META/DL/bdmt_*.xml</c>. Prefers the English variant
    /// (<c>bdmt_eng.xml</c>) and falls back to the first locale file
    /// found. Returns <c>null</c> when the disc has no embedded metadata
    /// or the xml cannot be parsed.
    /// </summary>
    internal string? TryReadBdmtTitle(string mountPath)
    {
        try
        {
            string trimmed = mountPath.TrimEnd('\\', '/');
            string dlDir = Path.Combine(trimmed, "BDMV", "META", "DL");

            if (!storageDriver.DirectoryExists(dlDir))
                return null;

            // Prefer English; fall back to the first locale present.
            string englishPath = Path.Combine(dlDir, "bdmt_eng.xml");
            string? xmlPath = storageDriver.FileExists(englishPath)
                ? englishPath
                : storageDriver
                    .EnumerateFileSystemEntries(dlDir, "bdmt_*.xml", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();

            if (xmlPath is null || !storageDriver.FileExists(xmlPath))
                return null;

            using Stream stream = storageDriver.OpenRead(xmlPath);
            using StreamReader reader = new(stream);
            string xmlContent = reader.ReadToEnd();
            XDocument doc = XDocument.Parse(xmlContent);
            XNamespace di = "urn:BDA:bdmv;discinfo";
            return doc.Descendants(di + "name").FirstOrDefault()?.Value;
        }
        catch (Exception ex)
        {
            logger.LogInformation(
                ex,
                "Could not read bdmt title from {Mount}: {Message}",
                mountPath,
                ex.Message
            );
            return null;
        }
    }

    private static string ToBlurayUrl(string mountPath)
    {
        if (mountPath.StartsWith("bluray:", StringComparison.OrdinalIgnoreCase))
            return mountPath;
        // libbluray needs a trailing separator on Windows: "bluray:D:/" works,
        // "bluray:D:" never enumerates playlists.
        string trimmed = mountPath.TrimEnd('\\', '/');
        return $"bluray:{trimmed}/";
    }

    private static string TrimStderr(string stdErr)
    {
        if (string.IsNullOrEmpty(stdErr))
            return "(no stderr)";
        string[] lines = stdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length <= 3 ? stdErr : string.Join('\n', lines[^3..]);
    }

    [GeneratedRegex(
        @"playlist\s+(?<index>\d+)\.mpls\s+\((?<duration>\d{1,}:\d{1,}:\d{1,})\)",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex PlaylistRegex();
}
