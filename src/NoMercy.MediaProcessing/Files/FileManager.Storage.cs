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
using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.TvShows;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.PostProcess;
using NoMercy.Encoder.Subtitles;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.MediaProcessing.Jobs.SubtitleJobs;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Extensions;
using NoMercy.Storage;
using NoMercyQueue;
using Serilog.Events;
using Logger = NoMercy.NmSystem.SystemCalls.Logger;
using QueueJobDispatcher = NoMercyQueue.JobDispatcher;

namespace NoMercy.MediaProcessing.Files;

public partial class FileManager
{
    private async Task StoreMusic()
    {
        List<MediaFile> items =
        [
            .. Files
                .SelectMany(file => file.Files ?? [])
                .Where(mediaFolder => mediaFolder.Parsed is not null),
        ];

        if (items.Count == 0)
            return;

        foreach (MediaFile item in items)
            await StoreAudioItem(item);

        Logger.App($"Found {items.Count} music files");
    }

    private async Task StoreMovie()
    {
        MediaFile? item = Files
            .SelectMany(file => file.Files ?? [])
            .FirstOrDefault(file => file.Parsed is not null);

        if (item == null)
            return;

        await StoreVideoItem(item);

        Logger.App($"Found {item.Path} for {Movie?.Title}");
    }

    private async Task StoreTvShow()
    {
        List<MediaFile> items =
        [
            .. Files
                .SelectMany(file => file.Files ?? [])
                .Where(mediaFolder => mediaFolder.Parsed is not null),
        ];

        Logger.App(
            $"[StoreTvShow] {Show?.Title} ({Show?.Id}): {items.Count} candidate files across {Folders.Count} folder(s)",
            LogEventLevel.Information
        );

        if (items.Count == 0)
            return;

        int idx = 0;
        foreach (MediaFile item in items)
        {
            idx++;
            Logger.App(
                $"[StoreVideoItem] {idx}/{items.Count} {Path.GetFileName(item.Path)}",
                LogEventLevel.Information
            );
            await StoreVideoItem(item);
        }

        Logger.App($"Found {items.Count} files for {Show?.Title}");
    }

    private async Task StoreAudioItem(MediaFile? item)
    {
        if (item?.Parsed is null)
            return;

        Folder? folder = Folders.FirstOrDefault(folder => item.Path.Contains(folder.Path));
        if (folder == null)
            return;

        await Task.CompletedTask;
    }

    private async Task StoreVideoItem(MediaFile item)
    {
        string itemPath = item.Path.Replace('\\', '/');
        Folder? folder = Folders.FirstOrDefault(f =>
            itemPath.Contains(f.Path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
        );
        if (folder == null)
        {
            Logger.App(
                $"[StoreVideoItem] no Folders match for {itemPath} — skipping (Folders={string.Join(", ", Folders.Select(f => f.Path))})",
                LogEventLevel.Warning
            );
            return;
        }

        // MediaScan resolves every path through the driver, so item.Path is
        // driver-absolute (e.g. "/mnt/vault/Media/Marvels/TV.Shows/..."). Every
        // storage call below is on the IStorage facade, which rejects absolute
        // keys on remote backends (StoragePathGuard). Rebase the path onto its
        // scope-relative root — the folder's stored Path — so the facade, the
        // stored HostFolder/Folder, and the served URLs are all backend-neutral.
        itemPath = StoragePathHelpers.RebaseToFolderRoot(itemPath, folder.Path);

        IStorage storage = StorageFor(folder);
        string fileName = "/" + storage.GetName(itemPath);
        string hostFolder = itemPath.Replace(fileName, "");

        // Folder is served as /{Share}{Folder}{Filename}, so it comes from the
        // library root. Finding the title's own folder name in the path fails
        // whenever disk spells it differently ("Oceans" for "Ocean's"), and the
        // storage path it fell back to is a row no client can play and the boot
        // sweep deletes.
        if (
            !StoragePathHelpers.TryGetLibraryRelativeFolder(
                hostFolder,
                folder.Path,
                out string baseFolder
            )
        )
        {
            Logger.App(
                $"[StoreVideoItem] {itemPath} does not resolve under library folder {folder.Path} — skipping",
                LogEventLevel.Warning
            );
            return;
        }

        List<Subtitle> subtitles = GetSubtitles(storage, hostFolder);

        DispatchOrphanedBitmapOcrBackfill(storage, folder, hostFolder);
        DispatchUndersizedSpriteUpgrade(storage, folder, hostFolder);

        List<VideoTrack> tracks = GetExtraFiles(storage, hostFolder);

        // The scan filtered itself down to one file (FilterFiles) and the caller
        // told us which episode it dispatched that file for (HintDispatchedMediaId).
        // Trust that known id over re-deriving the episode from the filename — the
        // filename parser can land on the wrong episode when the title itself
        // contains digits that read as a season/episode (e.g. South Park's "1%"
        // parsing as S00E12 instead of the dispatched S15E12). A file that
        // reached this point through any other route (initial import, manual
        // rescan, or a plugin-sourced file that skipped the filter) still goes
        // through the ordinary filename-derived lookup.
        bool isDispatchedTarget =
            Show is not null
            && Filter is not null
            && DispatchedMediaId is not null
            && item.Path.Contains(Filter, StringComparison.OrdinalIgnoreCase);

        Episode? episode = isDispatchedTarget
            ? await fileRepository.GetEpisodeById(DispatchedMediaId!.Value)
            : await fileRepository.GetEpisode(Show?.Id, item);

        // A show file that matches no episode still gets stored below, with neither an
        // episode nor a movie on it — nothing can list or play it, and because the
        // library only lists shows that have an episode with a playable file, the whole
        // show disappears. Without this the user is left with an empty library and
        // nowhere that says why. Seen with S00E00 specials, whose season/episode pair
        // exists in no provider's episode list.
        if (Show is not null && episode is null)
            await fileRepository.RecordUnmatchedEpisodeFileAsync(
                itemPath,
                Show.LibraryId,
                $"No episode matches season {item.Parsed?.Season?.ToString() ?? "?"} "
                    + $"episode {item.Parsed?.Episode?.ToString() ?? "?"} of {Show.Title}."
            );

        Metadata metadata = await MakeMetadata(
            storage,
            item,
            fileName,
            baseFolder,
            hostFolder,
            tracks
        );

        Ulid metadataId = await fileRepository.StoreMetadata(metadata);

        Logger.App($"Storing video file: {episode?.Id}, {Movie?.Id}", LogEventLevel.Verbose);

        // Joined multi-episode file (e.g. S01E01-E04): record the last covered
        // episode so every episode in the range counts as present, while a single
        // VideoFile keeps it from being listed twice in playlists.
        int? lastEpisodeNumber = null;
        if (episode is not null)
        {
            IReadOnlyList<int> covered = Parsing.EpisodeRangeParser.Expand(
                fileName,
                episode.SeasonNumber,
                episode.EpisodeNumber
            );
            if (covered.Count > 1)
                lastEpisodeNumber = covered[^1];
        }
        VideoFile videoFile = new()
        {
            EpisodeId = episode?.Id,
            LastEpisodeNumber = lastEpisodeNumber,
            MovieId = Movie?.Id,
            Folder = baseFolder.Replace("\\", "/"),
            HostFolder = hostFolder.Replace("\\", "/"),
            Filename = fileName.Replace("\\", "/"),

            Share = folder.Id.ToString(),
            Duration = Regex.Replace(
                Regex.Replace((item.FFprobe?.Duration.ToString()).OrEmpty(), @"\.\d+", ""),
                "^00:",
                ""
            ),
            // Chapters = JsonConvert.SerializeObject(item.FFprobe?.Chapters ?? []),
            Chapters = "",
            // Derive languages from the encoded audio tracks (the audio_<lang>_<codec>
            // dirs parsed into metadata.Audio), not the source ffprobe: a scan skips
            // ffprobe on remote drivers, so item.FFprobe is empty over NFS and this
            // column came out blank. The encoded tracks are the authoritative set of
            // playable languages anyway.
            Languages = JsonConvert.SerializeObject(
                (metadata.Audio ?? [])
                    .Select(audio => audio.Language)
                    .Where(language => !string.IsNullOrEmpty(language) && language != "und")
                    .Distinct()
            ),
            Quality = (item.FFprobe?.VideoStreams.FirstOrDefault()?.Width.ToString()).OrEmpty(),
            Subtitles = JsonConvert.SerializeObject(subtitles),
            Tracks = [.. tracks],
            MetadataId = metadataId,
        };

        await fileRepository.StoreVideoFile(videoFile);
    }

    /// <summary>
    /// Queues an OCR backfill for every preserved bitmap subtitle
    /// (<c>.sup</c>/<c>.sub</c>) under <paramref name="hostFolder"/> that has no
    /// text sibling carrying the same <c>{lang}.{variant}</c>. A title encoded
    /// before the OCR pipeline worked keeps its bitmap sidecar but never got a
    /// <c>.vtt</c>, so clients that cannot render PGS show nothing. OCR is minutes
    /// of ffmpeg per track, far too slow for the scan thread, so the actual work
    /// runs as a low-priority <see cref="SubtitleOcrBackfillJob"/> on the encoder
    /// queue; the job is idempotent, so re-queuing across scans until it lands is
    /// harmless. A bitmap track that already has an <c>.ass</c>/<c>.srt</c>/<c>.vtt</c>
    /// is left alone — OCRing it would double the track in the picker.
    /// </summary>
    private static void DispatchOrphanedBitmapOcrBackfill(
        IStorage storage,
        Folder folder,
        string hostFolder
    )
    {
        string subtitleFolder = storage.CombinePath(hostFolder, "subtitles");
        if (!storage.Exists(subtitleFolder))
            return;

        QueueJobDispatcher? dispatcher = QueueRunner.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        IReadOnlyList<OrphanedBitmapSubtitle> orphans = SelectOrphanedBitmapSubtitles(
            storage
                .List(subtitleFolder, null, recursive: false)
                .Where(candidate => !candidate.IsDirectory)
                .Select(candidate => storage.GetName(candidate.Path))
        );

        foreach ((string supName, string mediaTitle, string language, string variant) in orphans)
        {
            dispatcher.Dispatch(
                new SubtitleOcrBackfillJob(
                    folder.Id.ToString(),
                    folder.DriverId.ToString(),
                    hostFolder,
                    supName,
                    mediaTitle,
                    language,
                    variant
                ),
                "encoder",
                2
            );

            Logger.App(
                $"[SubtitleOcrBackfill] queued OCR for orphaned bitmap subtitle {supName}",
                LogEventLevel.Information
            );
        }
    }

    /// <summary>
    /// Queues a preview rebuild for a title whose sprite sheet was rendered at a
    /// narrower tile than the server produces now. Titles encoded when the tile
    /// was 160 wide show a scrub preview blown up several times over on a
    /// television; the sheet is a sidecar, so this costs one short ffmpeg pass
    /// and not a frame of re-encoded video.
    ///
    /// <para>Runs as a low-priority <see cref="SpriteSheetUpgradeJob"/> for the
    /// same reason the OCR backfill does — sampling a whole title is far too
    /// slow for the scan thread. The job re-checks the folder before doing
    /// anything, so re-queuing on every scan until it lands is harmless.</para>
    /// </summary>
    private static void DispatchUndersizedSpriteUpgrade(
        IStorage storage,
        Folder folder,
        string hostFolder
    )
    {
        QueueJobDispatcher? dispatcher = QueueRunner.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        IReadOnlyList<string> undersized = SpriteSheet.SelectUndersized(
            storage
                .List(hostFolder, null, recursive: false)
                .Where(entry => !entry.IsDirectory)
                .Select(entry => storage.GetName(entry.Path))
        );

        if (undersized.Count == 0)
            return;

        string title = storage.GetName(hostFolder);

        dispatcher.Dispatch(
            new SpriteSheetUpgradeJob(
                folder.Id.ToString(),
                folder.DriverId.ToString(),
                hostFolder,
                title
            ),
            "encoder",
            1
        );

        Logger.App(
            $"[SpriteSheetUpgrade] queued preview rebuild for {title} ({string.Join(", ", undersized)})",
            LogEventLevel.Information
        );
    }

    /// <summary>
    /// From a flat list of <c>subtitles/</c> filenames, returns each bitmap
    /// sidecar (<c>.sup</c>/<c>.sub</c>) that has no text track (<c>.vtt</c>,
    /// <c>.ass</c>, <c>.srt</c>, …) carrying the same <c>{lang}.{variant}</c>. A
    /// bitmap already backed by a text track is skipped so OCR never produces a
    /// duplicate track in the picker. The <c>MediaTitle</c> is the filename stem
    /// before <c>.{lang}.{variant}.{ext}</c>, reproduced on the OCR output so the
    /// scan pairs the new <c>.vtt</c> with its bitmap sibling.
    /// </summary>
    internal static IReadOnlyList<OrphanedBitmapSubtitle> SelectOrphanedBitmapSubtitles(
        IEnumerable<string> subtitleFileNames
    )
    {
        HashSet<string> textKeys = new(StringComparer.OrdinalIgnoreCase);
        List<OrphanedBitmapSubtitle> bitmaps = [];

        foreach (string name in subtitleFileNames)
        {
            Match match = SubtitleFileRegex().Match(name);
            if (!match.Success)
                continue;

            string language = match.Groups["lang"].Value;
            string variant = match.Groups["type"].Value;

            if (SubtitleClassifier.IsBitmapSidecarExtension(match.Groups["ext"].Value))
                bitmaps.Add(new(name, name[..match.Index].TrimEnd('.'), language, variant));
            else
                textKeys.Add($"{language}|{variant}");
        }

        return
        [
            .. bitmaps.Where(bitmap => !textKeys.Contains($"{bitmap.Language}|{bitmap.Variant}")),
        ];
    }

    private async Task<Metadata> MakeMetadata(
        IStorage storage,
        MediaFile item,
        string fileName,
        string baseFolder,
        string hostFolder,
        List<VideoTrack> extraFiles
    )
    {
        List<IVideo> video = GetVideoHashList(storage, hostFolder);
        List<IAudio> audio = GetAudioHashList(storage, hostFolder);
        List<ISubtitle> subtitles = GetSubtitleHashList(storage, hostFolder);
        List<IFont> fonts = GetFontHashList(storage, hostFolder);
        List<IPreview> previews = GetPreviewHashList(storage, hostFolder, extraFiles);

        await RebuildHlsMasterFromDiskAsync(storage, hostFolder, fileName, video);

        VideoTrack? chaptersFile = extraFiles.FirstOrDefault(file => file.Kind == "chapters");

        List<IChapter> chapters = chaptersFile?.File is not null
            ? await GetChapterHashListAsync(
                storage,
                hostFolder,
                Path.GetFileName(chaptersFile.File)
            )
            : [];

        IChapterFile? chaptersFileHashMap = chaptersFile?.File is not null
            ? new()
            {
                FileName = "/" + Path.GetFileName(chaptersFile.File).Replace("\\", "/"),
                FileSize = storage.SizeOrZero(
                    storage.CombinePath(hostFolder, Path.GetFileName(chaptersFile.File))
                ),
                FileHash = ComputeFileHash(
                    storage,
                    storage.CombinePath(hostFolder, Path.GetFileName(chaptersFile.File))
                ),
            }
            : null;

        IFontsFile? fontsFileHashMap = extraFiles
            .Where(file => file.Kind == "fonts")
            .Select(file => new IFontsFile
            {
                FileName = "/" + Path.GetFileName(file.File).Replace("\\", "/"),
                FileSize = storage.SizeOrZero(
                    storage.CombinePath(hostFolder, Path.GetFileName(file.File).OrEmpty())
                ),
                FileHash = ComputeFileHash(
                    storage,
                    storage.CombinePath(hostFolder, Path.GetFileName(file.File).OrEmpty())
                ),
            })
            .FirstOrDefault();

        Metadata metadata = new()
        {
            Filename = fileName.Replace("\\", "/"),
            Duration = (item.FFprobe?.Duration.ToString()).OrEmpty(),
            Folder = baseFolder.Replace("\\", "/"),
            HostFolder = hostFolder.Replace("\\", "/"),
            // Sum the sizes already gathered while building the hash lists rather
            // than walking every segment again: a rescan verifies completeness and
            // must not deep-dive the whole output tree over the network just to set
            // a storage-accounting stat. Covers every catalogued asset (video and
            // audio variants, subtitles, fonts, previews, chapters).
            FolderSize =
                video.Sum(entry => entry.FileSize ?? 0)
                + audio.Sum(entry => entry.FileSize ?? 0)
                + subtitles.Sum(entry => entry.FileSize ?? 0)
                + fonts.Sum(entry => entry.FileSize ?? 0)
                + previews.Sum(entry => entry.ImageFileSize + entry.TimeFileSize)
                + (chaptersFileHashMap?.FileSize ?? 0)
                + (fontsFileHashMap?.FileSize ?? 0),

            Type = Movie?.Id is not null
                ? Database.Models.Media.MediaType.Movie
                : Database.Models.Media.MediaType.Tv,

            Audio = audio,
            Previews = previews,
            Subtitles = subtitles,
            Video = video,
            Chapters = chapters,
            ChapterFile = chaptersFileHashMap,
            Fonts = fonts,
            FontsFile = fontsFileHashMap,
        };

        return metadata;
    }

    /// <summary>
    /// Rebuilds <c>{masterTitle}.m3u8</c> from the renditions actually on disk
    /// under <paramref name="hostFolder"/> — the fully-published media root a
    /// scan walks, with none of the encode-finalize path's staging/scope
    /// fragility (a coordinator's finalize can resolve against a scope that
    /// only sees the last bundle's own rendition when a preset gets split
    /// into several self-finalizing dispatch bundles; a completed scan always
    /// sees every rendition every bundle ever published). A no-op for a
    /// non-HLS item (<paramref name="video"/> empty — GetVideoHashList only
    /// ever populates from <c>video_*/</c> dirs, which only exist for HLS
    /// output). Best-effort: a rebuild failure must never fail the scan.
    /// </summary>
    private async Task RebuildHlsMasterFromDiskAsync(
        IStorage storage,
        string hostFolder,
        string fileName,
        List<IVideo> video
    )
    {
        if (video.Count == 0)
            return;

        string masterTitle = fileName.TrimStart('/');
        if (masterTitle.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            masterTitle = masterTitle[..^".m3u8".Length];

        // The rebuild ffprobes every video rendition over the network (a remote
        // backend copies each probe segment off first) and rewrites the master —
        // encode-finalize cost re-paid on every catalog scan. When the on-disk
        // master already advertises every rendition present on disk, that whole
        // pass reproduces the same master byte-for-byte, so skip it. Only a
        // genuinely incomplete master — the multi-bundle ladder split this exists
        // for, where a later bundle publishes a rung the current master never
        // listed — falls through to the full reconstruction.
        if (MasterCoversDiskOutput(storage, hostFolder, masterTitle, video))
            return;

        // Only logged when the rebuild actually runs (a missing or incomplete
        // master), so it stays quiet on the common already-complete rescan.
        Logger.App(
            $"[MasterRebuild] {masterTitle}: rebuilding (master missing or incomplete)",
            LogEventLevel.Information
        );

        try
        {
            HlsOnDiskPlanReconstructor reconstructor = new(mediaAnalyzer);
            OutputPlan plan = await reconstructor.ReconstructAsync(
                storage,
                hostFolder,
                CancellationToken.None
            );

            if (plan.VideoOutputs.Length == 0)
                return;

            HlsOutputStrategy hlsOutputStrategy = new(storage);
            await hlsOutputStrategy.FinalizeAsync(
                hostFolder,
                plan,
                masterTitle,
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            Logger.App(
                $"[RebuildHlsMasterFromDisk] {masterTitle}: master rebuild failed — {ex.Message}",
                LogEventLevel.Warning
            );
        }
    }

    /// <summary>
    /// True when the on-disk master <c>{masterTitle}.m3u8</c> already advertises
    /// every rendition present under <paramref name="hostFolder"/> — every
    /// <paramref name="video"/> and <c>audio_*/</c> playlist, and every
    /// <c>subtitles/{lang}/{variant}.{m3u8|ass|srt}</c> language-subdir track the
    /// master references (never the raw root sidecar the OCR pass drops; that
    /// surfaces through <see cref="GetSubtitles"/> on the VideoFile, not the
    /// master). In that state a rebuild reproduces the same master byte-for-byte,
    /// so the caller skips the per-rendition ffprobe + master rewrite. A missing
    /// master or any on-disk rendition the master does not list returns false so
    /// the rebuild runs — the multi-bundle self-heal, where a later bundle
    /// publishes a rung the current master never advertised. Only checks disk ⊆
    /// master: a rendition deleted after finalize leaves a stale over-reference the
    /// next real finalize prunes — not a scan concern.
    /// </summary>
    internal static bool MasterCoversDiskOutput(
        IStorage storage,
        string hostFolder,
        string masterTitle,
        List<IVideo> video
    )
    {
        string masterPath = storage.CombinePath(hostFolder, masterTitle + ".m3u8");
        if (!storage.Exists(masterPath))
            return false;

        string master;
        try
        {
            master = storage
                .ReadAllTextAsync(masterPath, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            return false;
        }

        foreach (IVideo entry in video)
        {
            string renditionDir = entry.FileName.TrimStart('/').Split('/')[0];
            if (!master.Contains(renditionDir + "/", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        foreach (
            StorageEntry dir in storage
                .List(hostFolder, "audio_*", recursive: false)
                .Where(entry => entry.IsDirectory)
        )
        {
            string renditionDir = storage.GetName(dir.Path);
            if (!master.Contains(renditionDir + "/", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        string subtitleFolder = storage.CombinePath(hostFolder, "subtitles");
        if (!storage.Exists(subtitleFolder))
            return true;

        foreach (
            StorageEntry languageDir in storage
                .List(subtitleFolder, "*", recursive: false)
                .Where(entry => entry.IsDirectory)
        )
        {
            string language = storage.GetName(languageDir.Path);
            foreach (
                StorageEntry track in storage
                    .List(languageDir.Path, "*", recursive: false)
                    .Where(entry => !entry.IsDirectory)
            )
            {
                string trackName = storage.GetName(track.Path);
                bool isAdvertised =
                    trackName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                    || trackName.EndsWith(".ass", StringComparison.OrdinalIgnoreCase)
                    || trackName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase);
                if (!isAdvertised)
                    continue;

                if (!master.Contains($"{language}/{trackName}", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }

        return true;
    }
}
