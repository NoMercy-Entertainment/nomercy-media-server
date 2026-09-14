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
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Encoder.Subtitles;
using NoMercy.NmSystem.Extensions;
using NoMercy.Storage;
using SixLabors.ImageSharp;
using Image = SixLabors.ImageSharp.Image;
using Logger = NoMercy.NmSystem.SystemCalls.Logger;

namespace NoMercy.MediaProcessing.Files;

public partial class FileManager
{
    private List<IVideo> GetVideoHashList(IStorage storage, string hostFolder)
    {
        List<IVideo> videos = [];

        if (!storage.Exists(hostFolder))
            return videos;

        // V3 encoder creates directories like video_1920x1080/ with .m3u8 playlist inside
        foreach (
            StorageEntry dir in storage
                .List(hostFolder, null, recursive: false)
                .Where(e => e.IsDirectory && storage.GetName(e.Path).StartsWith("video_"))
        )
        {
            string dirName = storage.GetName(dir.Path);
            Match match = VideoDirectoryRegex().Match(dirName);
            if (!match.Success)
                continue;

            int width = int.Parse(match.Groups["width"].Value);
            int height = int.Parse(match.Groups["height"].Value);

            // One directory listing serves both needs: locating the playlist and
            // summing the segment sizes. Over NFS each listing enumerates every
            // segment in the quality dir, so a second List here doubled the scan
            // cost per file for no new information.
            IReadOnlyList<StorageEntry> dirEntries = storage.List(dir.Path, null, recursive: false);

            StorageEntry? playlist = dirEntries.FirstOrDefault(e =>
                !e.IsDirectory
                && storage.GetName(e.Path).EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            );
            if (playlist is null)
                continue;

            string playlistPath = playlist.Path;
            long dirSize = dirEntries.Where(e => !e.IsDirectory).Sum(e => e.SizeBytes);

            videos.Add(
                new()
                {
                    Width = width,
                    Height = height,
                    FileName = $"/{storage.GetName(dir.Path)}/{storage.GetName(playlistPath)}",
                    FileHash = ComputeFileHash(storage, playlistPath),
                    FileSize = dirSize,
                }
            );
        }

        return videos;
    }

    private List<IAudio> GetAudioHashList(IStorage storage, string hostFolder)
    {
        List<IAudio> audioList = [];

        if (!storage.Exists(hostFolder))
            return audioList;

        // Encoder creates directories like audio_eng_aac/ with .m3u8 playlist inside
        foreach (
            StorageEntry dir in storage
                .List(hostFolder, null, recursive: false)
                .Where(e => e.IsDirectory && storage.GetName(e.Path).StartsWith("audio_"))
        )
        {
            string dirName = storage.GetName(dir.Path);
            Match match = AudioDirectoryRegex().Match(dirName);
            if (!match.Success)
                continue;

            string language = match.Groups["lang"].Value;
            // Old-naming dirs (`audio_jpn`) carry no codec token — default to aac,
            // the group the master's video stream references (AUDIO="audio_aac").
            string codec = match.Groups["codec"].Success ? match.Groups["codec"].Value : "aac";

            // Single listing for playlist lookup and size sum — see GetVideoHashList.
            IReadOnlyList<StorageEntry> dirEntries = storage.List(dir.Path, null, recursive: false);

            StorageEntry? playlist = dirEntries.FirstOrDefault(e =>
                !e.IsDirectory
                && storage.GetName(e.Path).EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            );
            if (playlist is null)
                continue;

            string playlistPath = playlist.Path;
            long dirSize = dirEntries.Where(e => !e.IsDirectory).Sum(e => e.SizeBytes);

            audioList.Add(
                new()
                {
                    Language = language,
                    Codec = codec,
                    FileName = $"/{storage.GetName(dir.Path)}/{storage.GetName(playlistPath)}",
                    FileHash = ComputeFileHash(storage, playlistPath),
                    FileSize = dirSize,
                }
            );
        }

        return audioList;
    }

    private List<ISubtitle> GetSubtitleHashList(IStorage storage, string hostFolder)
    {
        List<ISubtitle> subtitles = [];

        string subtitleFolder = storage.CombinePath(hostFolder, "subtitles");

        if (!storage.Exists(subtitleFolder))
            return subtitles;

        IReadOnlyList<StorageEntry> subtitleFiles = storage.List(
            subtitleFolder,
            null,
            recursive: false
        );
        foreach (StorageEntry subtitleEntry in subtitleFiles.Where(e => !e.IsDirectory))
        {
            Regex regex = SubtitleFileRegex();
            Match match = regex.Match(subtitleEntry.Path);

            if (!match.Success)
                continue;

            string path = subtitleEntry.Path;

            // Reject binary subtitle formats we can't stream as HLS sidecars; accept
            // every text format (vtt, ass, srt, ssa, sub, webvtt). The bitmap
            // track's OCR sidecar carries the same {lang}.{type} and is hashed in
            // its place, so the track is still represented here.
            string ext = match.Groups["ext"].Value;
            if (SubtitleClassifier.IsBitmapSidecarExtension(ext))
                continue;

            subtitles.Add(
                new()
                {
                    Language = match.Groups["lang"].Value,
                    Type = match.Groups["type"].Value,
                    // A client-facing sidecar URL, not a local disk path — must
                    // always join with '/' regardless of host OS. storage.CombinePath
                    // deliberately returns the driver-NATIVE separator (backslash for
                    // LocalStorageDriver on Windows, per IStorageDriver.DirectorySeparator),
                    // which is correct for on-disk I/O but produced a mixed "/subtitles\file"
                    // FileName on a Windows-hosted install — a malformed URL the player
                    // would fail to resolve.
                    FileName = $"/subtitles/{storage.GetName(path)}",
                    FileHash = ComputeFileHash(storage, path),
                    FileSize = subtitleEntry.SizeBytes,
                    Codec = ext,
                }
            );
        }

        return subtitles;
    }

    private List<IPreview> GetPreviewHashList(
        IStorage storage,
        string hostFolder,
        List<VideoTrack> extraFiles
    )
    {
        IEnumerable<IPreview> sprites = extraFiles
            .Where(file => file.Kind == "sprite")
            .Select(file =>
            {
                string spritePath = storage.CombinePath(
                    hostFolder,
                    Path.GetFileName(file.File).OrEmpty()
                );
                return new IPreview
                {
                    ImageFileName =
                        "/" + (Path.GetFileName(file.File).OrEmpty()).Replace("\\", "/"),
                    ImageFileSize = storage.SizeOrZero(spritePath),
                    ImageFileHash = ComputeFileHash(storage, spritePath),
                };
            });

        IEnumerable<IPreview> times = extraFiles
            .Where(file => file.Kind == "thumbnails")
            .Select(file =>
            {
                string vttPath = storage.CombinePath(
                    hostFolder,
                    Path.GetFileName(file.File).OrEmpty()
                );
                // Read + parse the VTT once — it was previously read twice, once
                // per dimension, doubling the NFS round-trips for the thumbnail track.
                (int Width, int Height) dimensions = GetImageDimensionsFromVtt(storage, vttPath);
                return new IPreview
                {
                    Width = dimensions.Width,
                    Height = dimensions.Height,
                    TimeFileName = "/" + (Path.GetFileName(file.File).OrEmpty()).Replace("\\", "/"),
                    TimeFileSize = storage.SizeOrZero(vttPath),
                    TimeFileHash = ComputeFileHash(storage, vttPath),
                };
            });

        List<IPreview> previews = sprites
            .Zip(
                times,
                (sprite, time) =>
                    new IPreview
                    {
                        Width = time.Width,
                        Height = time.Height,
                        ImageFileName = sprite.ImageFileName,
                        ImageFileSize = sprite.ImageFileSize,
                        ImageFileHash = sprite.ImageFileHash,
                        TimeFileName = time.TimeFileName,
                        TimeFileSize = time.TimeFileSize,
                        TimeFileHash = time.TimeFileHash,
                    }
            )
            .ToList();
        return previews;
    }

    private List<IFont> GetFontHashList(IStorage storage, string hostFolder)
    {
        string fontFolder = storage.CombinePath(hostFolder, "fonts");

        List<IFont> fonts = [];

        if (!storage.Exists(fontFolder))
            return fonts;

        IReadOnlyList<StorageEntry> fontFiles = storage.List(fontFolder, null, recursive: false);
        foreach (StorageEntry fontEntry in fontFiles.Where(e => !e.IsDirectory))
        {
            string path = fontEntry.Path;
            fonts.Add(
                new()
                {
                    // See the matching comment in GetSubtitleHashList: a client-facing
                    // sidecar URL must join with '/' regardless of host OS, never the
                    // driver-native separator storage.CombinePath deliberately returns.
                    FileName = $"/fonts/{storage.GetName(path)}",
                    FileHash = ComputeFileHash(storage, path),
                    FileSize = fontEntry.SizeBytes,
                }
            );
        }

        return fonts;
    }

    private async Task<List<IChapter>> GetChapterHashListAsync(
        IStorage storage,
        string hostFolder,
        string file
    )
    {
        string path = storage.CombinePath(hostFolder, file);

        List<IChapter> chapters = [];

        List<IChapter>? parsedChapters = await ParseChaptersAsync(storage, path);

        foreach (IChapter parsedChapter in parsedChapters ?? [])
        {
            chapters.Add(
                new()
                {
                    EndTime = parsedChapter.EndTime,
                    StartTime = parsedChapter.StartTime,
                    Title = parsedChapter.Title,
                    Id = parsedChapter.Id,
                }
            );
        }

        return chapters;
    }

    private static async Task MoveFolderAsync(
        string sourceFolder,
        string destinationFolder,
        IStorage sourceStorage,
        IStorage destinationStorage
    )
    {
        if (!sourceStorage.Exists(sourceFolder))
            throw new DirectoryNotFoundException($"Source folder not found: {sourceFolder}");

        bool sameBackend =
            ReferenceEquals(sourceStorage, destinationStorage)
            || sourceStorage.Driver.GetType() == destinationStorage.Driver.GetType();

        if (sameBackend)
        {
            sourceStorage.MoveDirectory(sourceFolder, destinationFolder);
            Logger.App($"Moved {sourceFolder} to {destinationFolder}");
            return;
        }

        IReadOnlyList<StorageEntry> entries = sourceStorage.List(
            sourceFolder,
            pattern: null,
            recursive: true
        );

        foreach (StorageEntry entry in entries)
        {
            if (entry.IsDirectory)
                continue;

            string relativePath = entry.Path.StartsWith(sourceFolder, StringComparison.Ordinal)
                ? entry.Path[sourceFolder.Length..].TrimStart('/', '\\')
                : entry.Path;

            string destPath = string.Join(
                '/',
                destinationFolder.TrimEnd('/'),
                relativePath.Replace('\\', '/')
            );

            string? parentDir = destinationStorage.GetParent(destPath);
            if (!string.IsNullOrEmpty(parentDir))
                destinationStorage.CreateDirectory(parentDir);

            await using Stream readStream = sourceStorage.OpenRead(entry.Path);
            await using Stream writeStream = destinationStorage.OpenWrite(
                destPath,
                overwrite: true
            );
            await readStream.CopyToAsync(writeStream);
        }

        sourceStorage.DeleteDirectory(sourceFolder, recursive: true);

        Logger.App($"Cross-backend move: {sourceFolder} to {destinationFolder}");
    }

    private static (int Width, int Height) GetImageDimensions(string filePath)
    {
        ImageInfo info = Image.Identify(filePath);

        return (info.Width, info.Height);
    }

    private static (int Width, int Height) GetImageDimensionsFromVtt(
        IStorage storage,
        string filePath
    )
    {
        string vttContents = storage
            .ReadAllTextAsync(filePath, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        Regex regex = ImageDimensions();
        Match match = regex.Match(vttContents);

        if (match.Success)
        {
            int width = int.Parse(match.Groups["width"].Value);
            int height = int.Parse(match.Groups["height"].Value);
            return (width, height);
        }

        return (0, 0);
    }

    private static async Task<List<IChapter>?> ParseChaptersAsync(
        IStorage storage,
        string chapterFile
    )
    {
        await using Stream fileStream = storage.OpenRead(chapterFile);
        using StreamReader reader = new(fileStream);
        string text = await reader.ReadToEndAsync();

        List<IChapter> chapters = ParseChaptersVtt(text);
        return chapters.Count == 0 ? null : chapters;
    }

    // Chapter files are WEBVTT (see ChapterWriter): a WEBVTT header, then
    // blank-line-separated cue blocks of an optional id line, a
    // `HH:MM:SS.mmm --> HH:MM:SS.mmm` timing line, and a title payload. The
    // general subtitle parser cannot read this shape and degrades to one
    // garbage entry per line, so chapters are parsed here directly.
    internal static List<IChapter> ParseChaptersVtt(string text)
    {
        List<IChapter> chapters = [];
        if (string.IsNullOrWhiteSpace(text))
            return chapters;

        string normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        string[] blocks = normalized.Split(["\n\n"], StringSplitOptions.None);

        int index = 0;
        foreach (string rawBlock in blocks)
        {
            string block = rawBlock.Trim();
            if (block.Length == 0 || block == "WEBVTT" || block.StartsWith("WEBVTT"))
                continue;
            if (block.StartsWith("NOTE") || block.StartsWith("STYLE") || block.StartsWith("REGION"))
                continue;

            string[] lines = block.Split('\n');

            int timingLineIndex = -1;
            Match? timing = null;
            for (int lineIndex = 0; lineIndex < Math.Min(2, lines.Length); lineIndex++)
            {
                Match candidate = ChapterTimingRegex().Match(lines[lineIndex].Trim());
                if (candidate.Success)
                {
                    timingLineIndex = lineIndex;
                    timing = candidate;
                    break;
                }
            }

            if (timing is null)
                continue;

            int start = ParseVttTimestampMs(timing.Groups[1].Value);
            int end = ParseVttTimestampMs(timing.Groups[2].Value);
            if (start < 0 || end < 0)
                continue;

            string title =
                lines.Length > timingLineIndex + 1
                    ? lines[timingLineIndex + 1].Trim()
                    : string.Empty;

            chapters.Add(
                new()
                {
                    Id = index++,
                    StartTime = start,
                    EndTime = end,
                    Title = title,
                }
            );
        }

        return NormalizeChapters(chapters);
    }

    // A chapter list should span the whole item from 0. Some sources mark only
    // a late chapter (e.g. a lone "Credits" cue starting at 29:58), which leaves
    // the player with a single marker floating near the end and no way to seek
    // the opening via chapters. When the first parsed chapter starts after 0,
    // prepend an opening chapter covering 0 -> firstStart so the timeline is
    // complete and every chapter set is usable. Ids are re-sequenced from 0.
    private static List<IChapter> NormalizeChapters(List<IChapter> chapters)
    {
        if (chapters.Count == 0)
            return chapters;

        if (chapters[0].StartTime > 0)
            chapters.Insert(
                0,
                new()
                {
                    StartTime = 0,
                    EndTime = chapters[0].StartTime,
                    Title = "Start",
                }
            );

        for (int i = 0; i < chapters.Count; i++)
            chapters[i].Id = i;

        return chapters;
    }

    // Parse a `HH:MM:SS.mmm` (or `MM:SS.mmm`) WEBVTT timestamp into whole
    // milliseconds. Returns -1 for unparseable input so the caller can skip it.
    private static int ParseVttTimestampMs(string timestamp)
    {
        string[] parts = timestamp.Split(':');
        double hours = 0;
        double minutes;
        double seconds;

        if (parts.Length == 3)
        {
            if (
                !double.TryParse(
                    parts[0],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out hours
                )
                || !double.TryParse(
                    parts[1],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out minutes
                )
                || !double.TryParse(
                    parts[2],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out seconds
                )
            )
                return -1;
        }
        else if (parts.Length == 2)
        {
            if (
                !double.TryParse(
                    parts[0],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out minutes
                )
                || !double.TryParse(
                    parts[1],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out seconds
                )
            )
                return -1;
        }
        else
        {
            return -1;
        }

        double totalSeconds = hours * 3600 + minutes * 60 + seconds;
        return (int)Math.Round(totalSeconds * 1000);
    }

    [GeneratedRegex(
        @"^((?:\d{1,3}:)?\d{2}:\d{2}(?:\.\d{1,3})?)\s+-->\s+((?:\d{1,3}:)?\d{2}:\d{2}(?:\.\d{1,3})?)"
    )]
    private static partial Regex ChapterTimingRegex();

    private static List<VideoTrack> GetExtraFiles(IStorage storage, string hostFolder)
    {
        List<VideoTrack> tracks = [];

        IReadOnlyList<StorageEntry> files = storage.List(hostFolder, null, recursive: false);

        // Index every thumb/sprite candidate first so we can pair a VTT with
        // a same-stem WEBP. Stale VTT files from a previous re-encode (e.g.
        // thumbs_320x178.vtt) used to be registered alongside the live sprite
        // (thumbs_320x180.webp), and the player followed the VTT's cues to a
        // non-existent webp — 404 every hover.
        Dictionary<string, string> spriteByStem = new(StringComparer.OrdinalIgnoreCase);
        List<(string Name, string Stem)> vttCandidates = [];

        // An empty sheet or cue file is a rebuild that was stopped before ffmpeg
        // wrote it; registering it replaces a working preview with nothing.
        foreach (StorageEntry entry in files.Where(e => !e.IsDirectory && !IsEmptyPreviewFile(e)))
        {
            string name = storage.GetName(entry.Path);
            string stem = storage.GetNameWithoutExtension(entry.Path);

            if (name.StartsWith("chapter"))
                tracks.Add(new() { File = "/" + name, Kind = "chapters" });
            else if (name.StartsWith("skipper"))
                tracks.Add(new() { File = "/" + name, Kind = "skippers" });
            else if (
                (
                    name.StartsWith("sprite")
                    || name.StartsWith("preview")
                    || name.StartsWith("thumb")
                ) && entry.Path.EndsWith("vtt")
            )
                vttCandidates.Add((name, stem));
            else if (
                (name.StartsWith("sprite") || name.StartsWith("thumb"))
                && entry.Path.EndsWith("webp")
            )
            {
                spriteByStem[stem] = name;
                tracks.Add(new() { File = "/" + name, Kind = "sprite" });
            }
            else if (name.StartsWith("fonts"))
                tracks.Add(new() { File = "/" + name, Kind = "fonts" });
        }

        // Only register VTTs whose basename has a matching sprite WEBP on
        // disk. Drops stale VTTs left behind when the sprite was re-rendered
        // at a different dimension and the old VTT wasn't cleaned up.
        // The older encoder named the pair sprite.webp + previews.vtt, so a stem
        // comparison alone drops that cue file and leaves a sheet no client can
        // read — the scrub bubble draws the time and no image.
        foreach ((string name, string stem) in vttCandidates)
        {
            bool paired =
                spriteByStem.ContainsKey(stem)
                || (
                    stem.Equals("previews", StringComparison.OrdinalIgnoreCase)
                    && spriteByStem.ContainsKey("sprite")
                );

            if (paired)
                tracks.Add(new() { File = "/" + name, Kind = "thumbnails" });
        }

        return tracks;
    }

    private static bool IsEmptyPreviewFile(StorageEntry entry) =>
        entry.SizeBytes == 0 && (entry.Path.EndsWith(".webp") || entry.Path.EndsWith(".vtt"));

    private static List<Subtitle> GetSubtitles(IStorage storage, string hostFolder)
    {
        string subtitleFolder = storage.CombinePath(hostFolder, "subtitles");

        List<Subtitle> subtitles = [];

        if (!storage.Exists(subtitleFolder))
            return subtitles;

        IReadOnlyList<StorageEntry> subtitleFiles = storage.List(
            subtitleFolder,
            null,
            recursive: false
        );

        // First pass: index every .vtt by {lang}|{type} so we can spot bitmap subs
        // (.sup / .vob) whose OCR pass left no companion .vtt behind. Without this
        // an orphaned bitmap silently disappears from the API track list and the
        // operator has no signal the OCR failed.
        HashSet<string> vttKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach (StorageEntry subtitleEntry in subtitleFiles.Where(e => !e.IsDirectory))
        {
            Match vttMatch = SubtitleFileRegex().Match(subtitleEntry.Path);
            if (vttMatch.Success && vttMatch.Groups["ext"].Value == "vtt")
                vttKeys.Add($"{vttMatch.Groups["lang"].Value}|{vttMatch.Groups["type"].Value}");
        }

        foreach (StorageEntry subtitleEntry in subtitleFiles.Where(e => !e.IsDirectory))
        {
            Regex regex = SubtitleFileRegex();
            Match match = regex.Match(subtitleEntry.Path);

            if (!match.Success)
                continue;

            // Reject binary subtitle formats; accept every text format.
            string ext = match.Groups["ext"].Value;
            if (SubtitleClassifier.IsBitmapSidecarExtension(ext))
            {
                string siblingKey = $"{match.Groups["lang"].Value}|{match.Groups["type"].Value}";
                if (!vttKeys.Contains(siblingKey))
                    Logger.App(
                        $"Orphaned bitmap subtitle (no sibling .vtt): {Path.GetFileName(subtitleEntry.Path)} — OCR likely failed or never ran"
                    );
                continue;
            }

            subtitles.Add(
                new()
                {
                    Language = match.Groups["lang"].Value,
                    Type = match.Groups["type"].Value,
                    Ext = ext,
                }
            );
        }

        return subtitles;
    }
}
