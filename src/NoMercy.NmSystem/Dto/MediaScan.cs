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
using MovieFileLibrary;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.FFProbe;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using Serilog.Events;

namespace NoMercy.NmSystem.Dto;

public class MediaScan : IDisposable, IAsyncDisposable
{
    private readonly MovieDetector _movieDetector = new();
    private readonly IStorageDriver _driver;

    private bool _fileListingEnabled;
    private bool _regexFilterEnabled = true;
    private string? Filter { get; set; }

    private readonly Regex _folderNameRegex = new(
        @"video_.*|audio_.*|subtitles|scans|cds.*|ost|album|music|original|fonts|thumbs|metadata|NCED|NCOP|\s\(\d\)\.|~",
        RegexOptions.IgnoreCase
    );

    private string[] _extensionFilter = [];

    public MediaScan(IStorageDriver driver)
    {
        _driver = driver;
    }

    public MediaScan EnableFileListing()
    {
        _fileListingEnabled = true;

        return this;
    }

    public MediaScan DisableRegexFilter()
    {
        _regexFilterEnabled = false;

        return this;
    }

    public MediaScan FilterByMediaType(string mediaType)
    {
        _extensionFilter = mediaType switch
        {
            "anime" or MediaTypes.TvMediaType or MediaTypes.MovieMediaType or "video" =>
            [
                ".mp4",
                ".avi",
                ".mkv",
                ".m3u8",
            ],
            "music" => [".mp3", ".flac", ".wav", ".m4a"],
            "subtitle" => [".srt", ".vtt", ".ass"],
            _ => throw new ArgumentOutOfRangeException(nameof(mediaType), mediaType, null),
        };

        return this;
    }

    public async Task<ConcurrentBag<MediaFolderExtend>> Process(string rootFolder, int depth = 0)
    {
        rootFolder = _driver.GetFullPath(rootFolder);
        return !_fileListingEnabled
            ? await Task.Run(() => ScanFoldersOnly(rootFolder, depth))
            : await ScanFolderAsync(rootFolder, depth);
    }

    private async Task<ConcurrentBag<MediaFolderExtend>> ScanFolderAsync(
        string folderPath,
        int depth
    )
    {
        folderPath = _driver.GetFullPath(folderPath);

        ConcurrentBag<MediaFolderExtend> folders = [];

        if (depth < 0)
            return folders;

        if (!_driver.DirectoryExists(folderPath))
            return folders;

        ConcurrentBag<MediaFile> files = await FilesAsync(folderPath);

        MovieFile movieFile1 = _movieDetector.GetInfo(folderPath);
        movieFile1.Year ??= folderPath.TryGetYear();

        folders.Add(
            new()
            {
                Name = Path.GetFileName(folderPath),
                Path = folderPath,
                Created = _driver.GetCreationTimeUtc(folderPath).ToLocalTime(),
                Modified = _driver.GetLastWriteTimeUtc(folderPath).ToLocalTime(),
                Accessed = _driver.GetLastAccessTimeUtc(folderPath).ToLocalTime(),
                Type = "folder",
                Parsed = new()
                {
                    Title = movieFile1.Title,
                    Year = movieFile1.Year,
                    FilePath = movieFile1.Path,
                },

                Files = files,
            }
        );

        try
        {
            IOrderedEnumerable<string>? directories = null;
            try
            {
                directories = _driver
                    .EnumerateFileSystemEntries(folderPath, "*", SearchOption.TopDirectoryOnly)
                    .Where(e => _driver.DirectoryExists(e))
                    .OrderBy(f => f);
            }
            catch
            {
                // ignored
            }
            if (directories is null)
                return folders;

            await Parallel.ForEachAsync(
                directories,
                SystemParallelism.Options,
                async (directory, cancellationToken) =>
                {
                    string folderName = Path.GetFileName(directory);

                    if ((_regexFilterEnabled && _folderNameRegex.IsMatch(folderName)) || depth == 0)
                    {
                        files.Add(
                            new()
                            {
                                Name = folderName,
                                Path = directory,
                                Created = _driver.GetCreationTimeUtc(directory).ToLocalTime(),
                                Modified = _driver.GetLastWriteTimeUtc(directory).ToLocalTime(),
                                Accessed = _driver.GetLastAccessTimeUtc(directory).ToLocalTime(),
                                Type = "folder",
                            }
                        );

                        return;
                    }

                    ConcurrentBag<MediaFile> files2 =
                        depth - 1 > 0 ? await FilesAsync(directory) : [];

                    string cleanedFolderName = StringExtensions
                        .RemoveBracketedString()
                        .Replace(folderName, string.Empty)
                        .Trim();
                    string cleanedDirectory = Path.Combine(
                        Path.GetDirectoryName(directory)!,
                        cleanedFolderName
                    );
                    MovieFile movieFile = _movieDetector.GetInfo(cleanedDirectory);
                    movieFile.Year ??= directory.TryGetYear();
                    if (string.IsNullOrEmpty(movieFile.Title))
                        movieFile.Title = cleanedFolderName.Replace('.', ' ').Trim();

                    folders.Add(
                        new()
                        {
                            Name = folderName,
                            Path = directory,
                            Created = _driver.GetCreationTimeUtc(directory).ToLocalTime(),
                            Modified = _driver.GetLastWriteTimeUtc(directory).ToLocalTime(),
                            Accessed = _driver.GetLastAccessTimeUtc(directory).ToLocalTime(),
                            Type = "folder",
                            Parsed = new()
                            {
                                Title = movieFile.Title,
                                Year = movieFile.Year,
                                FilePath = movieFile.Path,
                            },

                            Files = files2.Count > 0 ? files2 : null,

                            SubFolders =
                                depth - 1 > 0 ? await ScanFolderAsync(directory, depth - 1) : [],
                        }
                    );
                }
            );

            ConcurrentBag<MediaFolderExtend> response = folders
                .Where(f => f.Name is not "")
                .OrderByDescending(f => f.Name)
                .ToConcurrentBag();

            return response;
        }
        catch (Exception e)
        {
            Logger.App(e.Message, LogEventLevel.Fatal);
            throw;
        }
    }

    private ConcurrentBag<MediaFolderExtend> ScanFoldersOnly(string folderPath, int depth)
    {
        folderPath = _driver.GetFullPath(folderPath.ToUtf8());

        if (depth < 0)
            return [];

        try
        {
            ConcurrentBag<MediaFolderExtend> folders = [];

            IOrderedEnumerable<string> directories = _driver
                .EnumerateFileSystemEntries(folderPath, "*", SearchOption.TopDirectoryOnly)
                .Where(e => _driver.DirectoryExists(e))
                .OrderBy(f => f);

            Parallel.ForEach(
                directories,
                SystemParallelism.Options,
                (directory, _) =>
                {
                    string dir = _driver.GetFullPath(directory.ToUtf8());
                    Logger.App($"Scanning {dir}");

                    string folderName = Path.GetFileName(dir);

                    if (_regexFilterEnabled && _folderNameRegex.IsMatch(folderName))
                    {
                        folders.Add(
                            new()
                            {
                                Name = folderName,
                                Path = dir,
                                Created = _driver.GetCreationTimeUtc(dir).ToLocalTime(),
                                Modified = _driver.GetLastWriteTimeUtc(dir).ToLocalTime(),
                                Accessed = _driver.GetLastAccessTimeUtc(dir).ToLocalTime(),
                                Type = "folder",
                            }
                        );

                        return;
                    }

                    string cleanedFolderName = StringExtensions
                        .RemoveBracketedString()
                        .Replace(folderName, string.Empty)
                        .Trim();
                    string cleanedDirectory = Path.Combine(
                        Path.GetDirectoryName(directory)!,
                        cleanedFolderName
                    );
                    MovieFile movieFile = _movieDetector.GetInfo(cleanedDirectory);
                    movieFile.Year ??= directory.TryGetYear();
                    if (string.IsNullOrEmpty(movieFile.Title))
                        movieFile.Title = cleanedFolderName.Replace('.', ' ').Trim();

                    folders.Add(
                        new()
                        {
                            Name = folderName,
                            Path = directory,
                            Created = _driver.GetCreationTimeUtc(directory).ToLocalTime(),
                            Modified = _driver.GetLastWriteTimeUtc(directory).ToLocalTime(),
                            Accessed = _driver.GetLastAccessTimeUtc(directory).ToLocalTime(),
                            Type = "folder",

                            Parsed = new()
                            {
                                Title = movieFile.Title,
                                Year = movieFile.Year,
                                FilePath = movieFile.Path,
                            },

                            SubFolders = depth - 1 > 0 ? ScanFoldersOnly(directory, depth - 1) : [],
                        }
                    );
                }
            );

            ConcurrentBag<MediaFolderExtend> response = folders
                .Where(f => f.Name is not "")
                .OrderByDescending(f => f.Name)
                .ToConcurrentBag();

            return response;
        }
        catch (Exception e)
        {
            Logger.App(e.Message, LogEventLevel.Fatal);
            throw;
        }
    }

    private async Task<ConcurrentBag<MediaFile>> FilesAsync(string folderPath)
    {
        ConcurrentBag<MediaFile> files = [];
        try
        {
            IEnumerable<string> entries = _driver
                .EnumerateFileSystemEntries(folderPath, "*", SearchOption.TopDirectoryOnly)
                .Where(e => _driver.FileExists(e));

            await Parallel.ForEachAsync(
                entries,
                SystemParallelism.Options,
                async (file, cancellationToken) =>
                {
                    file = _driver.GetFullPath(file.ToUtf8());

                    if (Filter is not null)
                        if (!file.Contains(Filter))
                            return;

                    string extension = Path.GetExtension(file).ToLower();

                    if (_extensionFilter.Length > 0 && !_extensionFilter.Contains(extension))
                        return;

                    bool isVideoFile = extension is ".mp4" or ".avi" or ".mkv" or ".m3u8";
                    bool isAudioFile = extension is ".mp3" or ".flac" or ".wav" or ".m4a";
                    bool isSubtitleFile = extension is ".srt" or ".vtt" or ".ass" or ".sub";

                    if (!isVideoFile && !isAudioFile && !isSubtitleFile)
                        return;

                    MovieFile? movieFile =
                        isVideoFile || isAudioFile ? _movieDetector.GetInfo(file) : null;

                    // Override MovieDetector's season/episode with our own regex on the filename only,
                    // because MovieDetector can pick up season/episode numbers from parent folder segments
                    // in the full path (e.g. "S02 1080p" in the folder name instead of the file name).
                    if (movieFile is not null && (isVideoFile || isAudioFile))
                    {
                        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(file);
                        string cleanedFileName = StringExtensions
                            .RemoveBracketedString()
                            .Replace(fileNameWithoutExtension, string.Empty)
                            .Trim();

                        Match epMatch = StringExtensions
                            .MatchEpisodePrefix()
                            .Match(cleanedFileName);
                        if (!epMatch.Success)
                            epMatch = StringExtensions.MatchSeasonEpisode().Match(cleanedFileName);

                        // TryParse, not Parse: the patterns take any number of
                        // digits, and a name like Show.S01E20240101123000 does
                        // not fit an int. Thrown here, it ended the scan of the
                        // whole folder and its other files went missing (#243).
                        if (
                            epMatch is { Success: true, Groups.Count: >= 3 }
                            && int.TryParse(epMatch.Groups[1].Value, out int season)
                            && int.TryParse(epMatch.Groups[2].Value, out int episode)
                        )
                        {
                            movieFile.Season = season;
                            movieFile.Episode = episode;
                            movieFile.IsSeries = true;
                            movieFile.IsSuccess = true;
                        }
                        else
                        {
                            Match wordMatch = StringExtensions
                                .MatchEpisodeWord()
                                .Match(cleanedFileName);
                            if (
                                wordMatch.Success
                                && int.TryParse(wordMatch.Groups[1].Value, out int wordEpisode)
                            )
                            {
                                movieFile.Episode = wordEpisode;
                                movieFile.IsSeries = true;
                                movieFile.IsSuccess = true;
                            }
                        }
                    }

                    FfProbeData? ffprobe = null;
                    TagFile? tagFile = null;
                    try
                    {
                        if (isVideoFile || isAudioFile)
                        {
                            ffprobe = await FfProbe.CreateAsync(_driver, file, cancellationToken);
                            // Every reader of MediaFile.FFprobe takes null as "not
                            // probed". A failed probe kept as a result looked like
                            // a file with no streams and no duration (#320).
                            if (ffprobe.Failed)
                            {
                                Logger.App(
                                    $"Listing {file} without a probe: {string.Join("; ", ffprobe.ErrorData)}",
                                    LogEventLevel.Warning
                                );
                                ffprobe = null;
                            }
                            if (isAudioFile)
                            {
                                // TagFile needs random access — only safe on local FS.
                                if (_driver is LocalStorageDriver)
                                    tagFile = TagFile.Create(file);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.App(e.Message, LogEventLevel.Fatal);
                    }

                    MovieFileExtend movieFileExtend = new()
                    {
                        FilePath = movieFile?.Path ?? file,
                        Episode = movieFile?.Episode,
                        Year = movieFile?.Year,
                        Season = movieFile?.Season,
                        Title = movieFile?.Title,
                        IsSeries = movieFile?.IsSeries ?? false,
                        IsSuccess = movieFile?.IsSuccess ?? false,
                        DiscNumber = tagFile?.Tag?.Disc.ToInt() ?? 0,
                        TrackNumber = tagFile?.Tag?.Track.ToInt() ?? 0,
                    };

                    MediaFile res = new()
                    {
                        Name = Path.GetFileName(file),
                        Path = file,
                        Extension = extension,
                        Size = _driver.GetFileSize(file),
                        Created = _driver.GetCreationTimeUtc(file).ToLocalTime(),
                        Modified = _driver.GetLastWriteTimeUtc(file).ToLocalTime(),
                        Accessed = _driver.GetLastAccessTimeUtc(file).ToLocalTime(),
                        Type = "file",

                        Parsed = movieFileExtend,
                        FFprobe = ffprobe,
                        TagFile = tagFile,
                    };

                    files.Add(res);
                }
            );

            ConcurrentBag<MediaFile> response = files.OrderBy(f => f.Name).ToConcurrentBag();

            return response;
        }
        catch (Exception e)
        {
            Logger.App(e.Message, LogEventLevel.Fatal);

            return files;
        }
    }

    public void Dispose() { }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public MediaScan FilterByFileName(string? filter)
    {
        Filter = filter;

        return this;
    }
}
