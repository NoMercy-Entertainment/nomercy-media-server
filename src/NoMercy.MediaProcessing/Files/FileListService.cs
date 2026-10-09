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
using Microsoft.Extensions.Logging;
using MovieFileLibrary;
using NoMercy.MediaProcessing.Files.Parsing;
using NoMercy.NmSystem;
using NoMercy.NmSystem.FFProbe;
using NoMercy.Providers.TMDB.Models.Shared;
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.Files;

/// <summary>
/// Lists media files in a directory and projects them to FileItem (slice 16.6).
/// Enumerate -> parse -> IMediaIdentificationService.IdentifyAsync -> project.
/// A null identification result still emits a FileItem (Parsed populated, Match
/// empty): identification is enrichment, never a filter.
/// </summary>
public class FileListService(
    IStorageDriver storageDriver,
    IMediaIdentificationService identification,
    IFilenameParserPipeline filenameParser,
    ILogger<FileListService> logger
) : IFileListService
{
    private readonly FilenameResolver resolver = new(filenameParser);

    // Everything below the folder you point at, not just its top level. A show
    // keeps its episodes in per-season folders, so listing one level deep meant
    // picking a season, triaging it, going back, picking the next — for every
    // season of every show. One folder is the whole show.
    public FileInfo[] GetVideoFilesInDirectory(string directoryPath)
    {
        return
        [
            .. storageDriver
                .EnumerateFileSystemEntries(directoryPath, "*", SearchOption.AllDirectories)
                .Where(p => !storageDriver.DirectoryExists(p))
                .Where(p =>
                {
                    string ext = Path.GetExtension(p);
                    return ext is ".mkv" or ".mp4" or ".avi" or ".webm" or ".flv";
                })
                .Select(p => new FileInfo(p)),
        ];
    }

    public FileInfo[] GetAudioFilesInDirectory(string directoryPath)
    {
        return
        [
            .. storageDriver
                .EnumerateFileSystemEntries(directoryPath, "*", SearchOption.AllDirectories)
                .Where(p => !storageDriver.DirectoryExists(p))
                .Where(p =>
                {
                    string ext = Path.GetExtension(p);
                    return ext is ".mp3" or ".flac" or ".wav" or ".m4a";
                })
                .Select(p => new FileInfo(p)),
        ];
    }

    private static readonly string[] VideoExtensions = [".mkv", ".mp4", ".avi", ".webm", ".flv"];
    private static readonly string[] AudioExtensions = [".mp3", ".flac", ".wav", ".m4a"];

    public async Task<List<FileItem>> GetFilesInDirectory(string directoryPath, string libraryType)
    {
        FileInfo[] videoFiles = GetVideoFilesInDirectory(directoryPath);

        FileInfo[] audioFiles = GetAudioFilesInDirectory(directoryPath);

        ConcurrentBag<FileItem> fileList = [];
        if (videoFiles.Length == 0 && audioFiles.Length == 0)
            return [.. fileList];

        if (audioFiles.Length > 0 && videoFiles.Length == 0)
        {
            // An album is a folder, so the album name comes from each track's own
            // folder rather than the one being browsed. Reading it from the browse
            // root was fine while listing stopped at one level; now that it
            // descends, pointing at an artist would have stamped the first
            // album's name across their whole discography.
            await Parallel.ForEachAsync(
                audioFiles,
                SystemParallelism.Options,
                (file, _cancellation) =>
                {
                    string albumFolder = file.DirectoryName ?? directoryPath;
                    (int year, string albumName, _, _) = MusicPathParser.Parse(
                        albumFolder,
                        Path.GetFileName(albumFolder)
                    );

                    fileList.Add(
                        new()
                        {
                            Size = file.Length,
                            Mode = (int)file.Attributes,
                            Name = Path.Combine(albumFolder, file.Name),
                            Parent = albumFolder,
                            Parsed = new(albumFolder)
                            {
                                Title =
                                    albumName + " - " + Path.GetFileNameWithoutExtension(file.Name),
                                Year = year.ToString(),
                                IsSeries = false,
                                IsSuccess = true,
                            },
                            Match = new() { Title = albumName },
                            Path = file.FullName,
                        }
                    );
                    return ValueTask.CompletedTask;
                }
            );
        }
        else if (videoFiles.Length > 0)
        {
            // Process video files sequentially - each file may add shows/movies to the DB,
            // and parallel processing causes race conditions when the show isn't known yet
            // (multiple iterations see "not found" simultaneously, all try to add, some fail)
            foreach (FileInfo file in videoFiles)
            {
                try
                {
                    await ProcessVideoFileInfo(libraryType, file, fileList);
                }
                catch (Exception e)
                {
                    logger.LogError(e.Message);
                }
            }
        }

        return [.. fileList.OrderBy(file => file.Name)];
    }

    /// <summary>
    /// Driver-aware variant of <see cref="GetFilesInDirectory"/>. Uses
    /// <paramref name="storage"/> to enumerate entries so the path never
    /// touches the local filesystem — required for NFS / S3 / WebDAV sources.
    /// ffprobe receives a real local path via <see cref="IStorage.AcquireLocalPath"/>.
    /// </summary>
    public async Task<List<FileItem>> GetFilesInDirectory(
        string directoryPath,
        string libraryType,
        IStorage storage
    )
    {
        IReadOnlyList<StorageEntry> allEntries = storage.List(
            directoryPath,
            pattern: null,
            recursive: true
        );

        StorageEntry[] videoEntries =
        [
            .. allEntries.Where(e =>
                !e.IsDirectory
                && VideoExtensions.Contains(
                    Path.GetExtension(e.Path),
                    StringComparer.OrdinalIgnoreCase
                )
            ),
        ];

        StorageEntry[] audioEntries =
        [
            .. allEntries.Where(e =>
                !e.IsDirectory
                && AudioExtensions.Contains(
                    Path.GetExtension(e.Path),
                    StringComparer.OrdinalIgnoreCase
                )
            ),
        ];

        ConcurrentBag<FileItem> fileList = [];

        if (videoEntries.Length == 0 && audioEntries.Length == 0)
            return [.. fileList];

        if (audioEntries.Length > 0 && videoEntries.Length == 0)
        {
            // Same as the local path: an album is a folder, so each track's album
            // is read from the folder the track is in, not the one being browsed.
            await Parallel.ForEachAsync(
                audioEntries,
                SystemParallelism.Options,
                (entry, _cancellation) =>
                {
                    string albumFolder = StoragePathHelpers.GetParent(entry.Path) ?? directoryPath;
                    (int year, string albumName, _, _) = MusicPathParser.Parse(
                        albumFolder,
                        StoragePathHelpers.GetName(albumFolder)
                    );

                    fileList.Add(
                        new()
                        {
                            Size = entry.SizeBytes,
                            Mode = 0,
                            Name = entry.Path,
                            Parent = albumFolder,
                            Parsed = new(albumFolder)
                            {
                                Title =
                                    albumName
                                    + " - "
                                    + StoragePathHelpers.GetNameWithoutExtension(entry.Path),
                                Year = year.ToString(),
                                IsSeries = false,
                                IsSuccess = true,
                            },
                            Match = new() { Title = albumName },
                            Path = entry.Path,
                        }
                    );
                    return ValueTask.CompletedTask;
                }
            );
        }
        else if (videoEntries.Length > 0)
        {
            foreach (StorageEntry entry in videoEntries)
            {
                try
                {
                    await ProcessVideoStorageEntry(libraryType, entry, storage, fileList);
                }
                catch (Exception e)
                {
                    logger.LogError(e.Message);
                }
            }
        }

        return [.. fileList.OrderBy(file => file.Name)];
    }

    private async Task<bool> ProcessVideoStorageEntry(
        string libraryType,
        StorageEntry entry,
        IStorage storage,
        ConcurrentBag<FileItem> fileList
    )
    {
        string entryPath = entry.Path;
        string fileName = StoragePathHelpers.GetName(entryPath);
        string? directoryName = StoragePathHelpers.GetParent(entryPath);

        // Filelist runs FOR EVERY FILE the user wants to triage; ffprobe over
        // a non-seekable stdin pipe scans to EOF on container formats whose
        // duration lives at end-of-file (mp4 mvhd, etc.) — 30 s/file on NFS.
        // The encode queue probes the full file later anyway. Skip ffprobe
        // here for remote drivers; the only reliable consumer of Streams in
        // the filelist response is the local-encode pre-flight which still
        // works end-to-end for LocalStorage.
        FfProbeData ffprobeData =
            storage.Driver is Storage.Drivers.Local.LocalStorageDriver
                ? await FfProbe.CreateAsync(entryPath)
                : new();

        ResolvedName resolved = resolver.Resolve(fileName, directoryName, entryPath, libraryType);
        MovieFile parsed = resolved.Parsed;
        if (parsed.Title == null)
            return true;

        (MovieOrEpisode episodeMatch, string? imdbId)? result = await identification.IdentifyAsync(
            parsed,
            libraryType,
            ffprobeData.Format.Duration,
            resolved.OverrideTmdbId,
            resolved.SeasonExplicit,
            resolved.AirDate
        );

        MovieOrEpisode match = new();
        if (result != null)
        {
            parsed.ImdbId = result.Value.imdbId;
            match = result.Value.episodeMatch;
        }

        // For driver-relative paths the "parent" is the directory that contains
        // the season folder, i.e. one level above directoryName.
        string? parentPath = string.IsNullOrEmpty(directoryName)
            ? "/"
            : StoragePathHelpers.GetParent(directoryName) ?? "/";

        ApplyEpisodeCardLabel(parsed, match);

        fileList.Add(
            new()
            {
                Size = entry.SizeBytes,
                Mode = 0,
                Name = StoragePathHelpers.GetNameWithoutExtension(fileName),
                Parent = parentPath,
                Parsed = parsed,
                Match = match,
                Path = entryPath,
                Streams = new()
                {
                    Video = ffprobeData.VideoStreams.Select(video => new Video
                    {
                        Index = video.Index,
                        Width = video.Width,
                        Height = video.Height,
                    }),
                    Audio = ffprobeData.AudioStreams.Select(stream => new Audio
                    {
                        Index = stream.Index,
                        Language = stream.Language,
                    }),
                    Subtitle = ffprobeData.SubtitleStreams.Select(stream => new Subtitle
                    {
                        Index = stream.Index,
                        Language = stream.Language ?? "und",
                    }),
                },
            }
        );

        return result != null;
    }

    /// <summary>
    /// Replaces parsed.Title with the canonical TMDB English show name so the
    /// dashboard's '<parsed.title> SxxExx - <match.title>' template renders a
    /// label consistent with Stoney's file-naming convention even when the
    /// source filename uses a transliterated / fan-sub title.
    /// Movies and unmatched items keep their filename-derived parsed.Title.
    /// </summary>
    private static void ApplyEpisodeCardLabel(MovieFile parsed, MovieOrEpisode match)
    {
        if (string.IsNullOrWhiteSpace(match.ShowName))
            return;

        parsed.Title = match.ShowName;
    }

    private async Task<bool> ProcessVideoFileInfo(
        string libraryType,
        FileInfo file,
        ConcurrentBag<FileItem> fileList
    )
    {
        FfProbeData ffprobeData = await FfProbe.CreateAsync(file.FullName);

        ResolvedName resolved = resolver.Resolve(
            file.Name,
            file.DirectoryName,
            file.FullName,
            libraryType
        );
        MovieFile parsed = resolved.Parsed;
        if (parsed.Title == null)
            return true;

        (MovieOrEpisode episodeMatch, string? imdbId)? result = await identification.IdentifyAsync(
            parsed,
            libraryType,
            ffprobeData.Format.Duration,
            resolved.OverrideTmdbId,
            resolved.SeasonExplicit,
            resolved.AirDate
        );

        MovieOrEpisode match = new();
        if (result != null)
        {
            parsed.ImdbId = result.Value.imdbId;
            match = result.Value.episodeMatch;
        }
        fileList.Add(BuildFileItem(file, parsed, match, ffprobeData));

        return result != null;
    }

    private static FileItem BuildFileItem(
        FileInfo file,
        MovieFile parsed,
        MovieOrEpisode match,
        FfProbeData ffprobeData
    )
    {
        string? parentPath = string.IsNullOrEmpty(file.DirectoryName)
            ? "/"
            : Path.GetDirectoryName(Path.Combine(file.DirectoryName, ".."));

        ApplyEpisodeCardLabel(parsed, match);

        return new()
        {
            Size = file.Length,
            Mode = (int)file.Attributes,
            Name = Path.GetFileNameWithoutExtension(file.Name),
            Parent = parentPath,
            Parsed = parsed,
            Match = match,
            Path = file.FullName,
            Streams = new()
            {
                Video = ffprobeData.VideoStreams.Select(video => new Video
                {
                    Index = video.Index,
                    Width = video.Width,
                    Height = video.Height,
                }),
                Audio = ffprobeData.AudioStreams.Select(stream => new Audio
                {
                    Index = stream.Index,
                    Language = stream.Language,
                }),
                Subtitle = ffprobeData.SubtitleStreams.Select(stream => new Subtitle
                {
                    Index = stream.Index,
                    Language = stream.Language ?? "und",
                }),
            },
        };
    }
}
