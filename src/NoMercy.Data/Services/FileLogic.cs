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

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.Encoder.Subtitles;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Extensions;
using NoMercy.Storage;

namespace NoMercy.Data.Services;

public partial class FileLogic(
    int id,
    Library library,
    MediaContext mediaContext,
    IStorageFactory storageFactory,
    ILogger<FileLogic> logger
) : IDisposable, IAsyncDisposable
{
    private int Id { get; set; } = id;
    private Library Library { get; set; } = library;
    private Movie? Movie { get; set; }
    private Tv? Show { get; set; }

    private List<Folder> Folders { get; set; } = [];
    public List<MediaFolderExtend> Files { get; set; } = [];
    public string Type { get; set; } = "";

    public async Task Process()
    {
        await MediaType();
        Paths();

        foreach (Folder folder in Folders)
        {
            ConcurrentBag<MediaFolderExtend> files = await GetFiles(folder);

            if (!files.IsEmpty)
                Files.AddRange(files);
        }

        switch (Library.Type)
        {
            case MediaTypes.MovieMediaType:
                await StoreMovie();
                break;
            case MediaTypes.TvMediaType:
            case MediaTypes.AnimeMediaType:
                await StoreTvShow();
                break;
            case MediaTypes.MusicMediaType:
                await StoreMusic();
                break;
            default:
                logger.LogInformation("Unknown library type");
                break;
        }
    }

    private async Task StoreMusic()
    {
        MediaFile? item = Files
            .FirstOrDefault(file => file.Parsed.Title is not null)
            ?.Files?.FirstOrDefault(file => file.Parsed is not null);

        if (item == null)
            return;

        await StoreAudioItem(item);
    }

    private async Task StoreMovie()
    {
        MediaFile? item = Files
            .SelectMany(file => file.Files ?? [])
            .FirstOrDefault(file => file.Parsed is not null);

        if (item == null)
            return;

        await StoreVideoItem(item);
    }

    private async Task StoreTvShow()
    {
        List<MediaFile> items = Files
            .SelectMany(file => file.Files ?? [])
            .Where(mediaFolder => mediaFolder.Parsed is not null)
            .ToList();

        if (items.Count == 0)
            return;

        foreach (MediaFile item in items)
            await StoreVideoItem(item);
    }

    public class Subtitle
    {
        [JsonProperty("language")]
        public string Language { get; set; } = string.Empty;

        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;

        [JsonProperty("ext")]
        public string Ext { get; set; } = string.Empty;
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

    private async Task StoreVideoItem(MediaFile? item)
    {
        if (item?.Parsed is null)
            return;

        Folder? folder = Folders.FirstOrDefault(folder => item.Path.Contains(folder.Path));
        if (folder == null)
            return;

        List<Subtitle> subtitles = [];

        // MediaScan resolves paths through the driver, so item.Path is
        // driver-absolute. Rebase onto the scope-relative folder root so the
        // facade calls below (ExistsAsync / List) accept it on remote backends
        // and the persisted path stays backend-neutral.
        string itemPath = StoragePathHelpers.RebaseToFolderRoot(
            item.Path.Replace('\\', '/'),
            folder.Path
        );
        string fileName = "/" + StoragePathHelpers.GetName(itemPath);
        string hostFolder = itemPath.Replace(fileName, "");

        // Folder is served as /{Share}{Folder}{Filename}, so it has to be
        // root-exclusive. Storing the raw scan path instead produces a row whose
        // URL no client can resolve.
        if (
            !StoragePathHelpers.TryGetLibraryRelativeFolder(
                hostFolder,
                folder.Path,
                out string baseFolder
            )
        )
        {
            logger.LogWarning(
                "Skipping video file '{Path}': it does not resolve under library folder '{Root}'",
                item.Path,
                folder.Path
            );
            return;
        }

        string subtitleFolder = hostFolder.TrimEnd('/') + "/subtitles";

        IStorage storage = storageFactory.For(folder.Id, folder.DriverId, string.Empty);
        if (await storage.ExistsAsync(subtitleFolder, CancellationToken.None))
        {
            IReadOnlyList<StorageEntry> subtitleEntries = storage.List(subtitleFolder, "*", false);
            foreach (string subtitleFile in subtitleEntries.Select(e => e.Path))
            {
                Regex regex = SubtitleFileTagsRegex();
                Match match = regex.Match(subtitleFile);

                if (!match.Success)
                    continue;

                // Reject binary subtitle formats we can't stream as HLS sidecars;
                // accept every text format (vtt, ass, srt, ssa, sub, webvtt) and
                // every variant (sign, full, sdh, alt, ...). The bitmap track's
                // own OCR sidecar carries the same {lang}.{type}, so dropping it
                // here loses nothing: the readable .vtt is listed in its place.
                string ext = match.Groups["ext"].Value;
                if (SubtitleClassifier.IsBitmapSidecarExtension(ext))
                    continue;

                subtitles.Add(
                    new()
                    {
                        Language = match.Groups["lang"].Value,
                        Type = match.Groups["type"].Value,
                        Ext = ext,
                    }
                );
            }
        }

        Episode? episode = await mediaContext
            .Episodes.Where(e => Show != null && e.TvId == Show.Id)
            .Where(e => e.SeasonNumber == item.Parsed.Season)
            .Where(e => e.EpisodeNumber == item.Parsed.Episode)
            .FirstOrDefaultAsync();

        try
        {
            VideoFile videoFile = new()
            {
                EpisodeId = episode?.Id,
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
                Languages = JsonConvert.SerializeObject(
                    item.FFprobe?.AudioStreams.Select(stream => stream.Language)
                        .Where(stream => stream != null && stream != "und")
                ),
                Quality = (item.FFprobe?.VideoStreams.FirstOrDefault()?.Width.ToString()).OrEmpty(),
                Subtitles = JsonConvert.SerializeObject(subtitles),
            };

            if (HostFolderPath.ContainsSecondRoot(videoFile.HostFolder))
            {
                logger.LogError(
                    "Skipping video file {Filename}: host folder holds a second root: '{HostFolder}'",
                    videoFile.Filename,
                    videoFile.HostFolder
                );
                return;
            }

            await mediaContext
                .VideoFiles.Upsert(videoFile)
                .On(vf => vf.Filename)
                .WhenMatched(
                    (vs, vi) =>
                        new()
                        {
                            Id = vi.Id,
                            EpisodeId = vi.EpisodeId,
                            MovieId = vi.MovieId,
                            Folder = vi.Folder,
                            HostFolder = vi.HostFolder,
                            Filename = vi.Filename,
                            Share = vi.Share,
                            Duration = vi.Duration,
                            Chapters = vi.Chapters,
                            Languages = vi.Languages,
                            Quality = vi.Quality,
                            Subtitles = vi.Subtitles,
                        }
                )
                .RunAsync();
        }
        catch (Exception e)
        {
            logger.LogError(e.Message);
        }
    }

    private async Task MediaType()
    {
        switch (Library.Type)
        {
            case MediaTypes.MovieMediaType:
                Movie = await mediaContext.Movies.Where(m => m.Id == Id).FirstOrDefaultAsync();
                Type = MediaTypes.MovieMediaType;
                break;
            case MediaTypes.TvMediaType:
                Show = await mediaContext.Tvs.Where(t => t.Id == Id).FirstOrDefaultAsync();
                Type = MediaTypes.TvMediaType;
                break;
            case MediaTypes.AnimeMediaType:
                Show = await mediaContext.Tvs.Where(t => t.Id == Id).FirstOrDefaultAsync();
                Type = MediaTypes.AnimeMediaType;
                break;
        }
    }

    private async Task<ConcurrentBag<MediaFolderExtend>> GetFiles(Folder folder)
    {
        // Resolve the per-folder driver so NFS/SMB folders use the right backend.
        IStorage folderStorage = storageFactory.For(folder.Id, folder.DriverId, string.Empty);
        MediaScan mediaScan = new(folderStorage.Driver);

        int depth = Library.Type switch
        {
            MediaTypes.MovieMediaType => 1,
            MediaTypes.TvMediaType => 2,
            MediaTypes.AnimeMediaType => 2,
            _ => 1,
        };

        // Resolve through the driver, not the IStorage facade: the facade's
        // GetFullPath is a LocalStorage-only escape hatch that throws on every
        // remote backend, so a facade call here killed every rescan of an
        // NFS / SMB / S3 / WebDAV library. The driver resolves the path within
        // its own backend, exactly as MediaScan.Process does internally.
        string scanRoot = folderStorage.Driver.GetFullPath(folder.Path);

        ConcurrentBag<MediaFolderExtend> folders = await mediaScan
            .EnableFileListing()
            .FilterByMediaType(Library.Type)
            .Process(scanRoot, depth);

        await mediaScan.DisposeAsync();

        return folders;
    }

    private void Paths()
    {
        string? folder = Library.Type switch
        {
            MediaTypes.MovieMediaType => Movie?.Folder?.Replace("/", ""),
            MediaTypes.TvMediaType => Show?.Folder?.Replace("/", ""),
            MediaTypes.AnimeMediaType => Show?.Folder?.Replace("/", ""),
            _ => "",
        };

        if (folder == null)
            return;

        Folder[] rootFolders = Library.FolderLibraries.Select(f => f.Folder).ToArray();

        foreach (Folder rootFolder in rootFolders)
        {
            IStorage folderStorage = storageFactory.For(
                rootFolder.Id,
                rootFolder.DriverId,
                string.Empty
            );
            // Stay in scope-relative space: rootFolder.Path is the storage key
            // the facade wants (e.g. "Marvels/TV.Shows"); the facade Exists / List
            // reject absolute paths. Do NOT resolve to an OS/mount-absolute path
            // here — GetFullPath produces "/mnt/vault/..." which StoragePathGuard
            // then rejects as a scope-relative key.
            string path = folderStorage.CombinePath(rootFolder.Path, folder);

            if (!folderStorage.Exists(path))
            {
                // FindMatchingDirectory walks the raw driver, so it needs the
                // driver-absolute root and returns a driver-absolute directory.
                // Convert that hit back to a scope-relative key before storing.
                string resolvedRoot = folderStorage.Driver.GetFullPath(rootFolder.Path);
                string? match = FileNameSanitizer.FindMatchingDirectory(
                    folderStorage.Driver,
                    resolvedRoot,
                    folder
                );
                if (match != null)
                    path = folderStorage.CombinePath(
                        rootFolder.Path,
                        folderStorage.GetName(match.Replace('\\', '/'))
                    );
            }

            if (folderStorage.Exists(path))
                Folders.Add(
                    new()
                    {
                        Path = path,
                        Id = rootFolder.Id,
                        DriverId = rootFolder.DriverId,
                    }
                );
        }
    }

    // Match the encoder's subtitle filename scheme: {lang}.{variant}.{ext}
    // anywhere in the filename tail. 2-3 char lang (ISO 639-1/2), any-length
    // variant (sign, full, sdh, alt, forced, …), 3-6 char extension (vtt,
    // ass, srt, ssa, sub, idx, webvtt).
    [GeneratedRegex(@"(?<lang>[a-zA-Z]{2,3})\.(?<type>\w+)\.(?<ext>\w{3,6})$")]
    private static partial Regex SubtitleFileTagsRegex();

    public void Dispose()
    {
        mediaContext.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await mediaContext.DisposeAsync();
    }
}
