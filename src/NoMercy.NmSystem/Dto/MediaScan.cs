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
using NoMercy.MediaProcessing.Files.Parsing;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.FFProbe;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Drivers.Smb;
using Serilog.Events;

namespace NoMercy.NmSystem.Dto;

public partial class MediaScan : IDisposable, IAsyncDisposable
{
    private readonly MovieDetector _movieDetector = new();
    private readonly IStorageDriver _driver;

    private bool _fileListingEnabled;
    private bool _regexFilterEnabled = true;
    private string? Filter { get; set; }

    [GeneratedRegex(
        @"video_.*|audio_.*|subtitles|scans|cds.*|ost|album|music|original|fonts|thumbs|metadata|NCED|NCOP|\s\(\d\)\.|~",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex FolderNameRegex();

    // A title library lists fewer formats than the watcher accepts; an inbox scan
    // swaps in the shared lists so it lists what the watcher and classifier take.
    private static readonly string[] TitleVideoExtensions = [".mp4", ".avi", ".mkv", ".m3u8"];
    private static readonly string[] TitleAudioExtensions = [".mp3", ".flac", ".wav", ".m4a"];

    private string[] _extensionFilter = [];
    private string[] _videoExtensions = TitleVideoExtensions;
    private string[] _audioExtensions = TitleAudioExtensions;
    private string _libraryType = MediaTypes.TvMediaType;

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
        if (mediaType is "anime" or MediaTypes.TvMediaType or MediaTypes.MovieMediaType)
            _libraryType = mediaType;

        if (mediaType is MediaTypes.InboxMediaType)
        {
            _videoExtensions = MediaFileExtensions.Video;
            _audioExtensions = MediaFileExtensions.Audio;
        }

        _extensionFilter = mediaType switch
        {
            "anime" or MediaTypes.TvMediaType or MediaTypes.MovieMediaType or "video" =>
                TitleVideoExtensions,
            "music" => TitleAudioExtensions,
            MediaTypes.InboxMediaType => MediaFileExtensions.Inbox,
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
                directories = (
                    _driver is SmbStorageDriver
                        ? _driver
                            .EnumerateEntries(folderPath, "*", SearchOption.TopDirectoryOnly)
                            .Where(entry => entry.IsDirectory)
                            .Select(entry => entry.Path)
                        : _driver
                            .EnumerateFileSystemEntries(
                                folderPath,
                                "*",
                                SearchOption.TopDirectoryOnly
                            )
                            .Where(entry => _driver.DirectoryExists(entry))
                ).OrderBy(f => f);
            }
            catch (Exception e)
            {
                Logger.App(
                    $"Could not list folders in {folderPath}: {e.GetType().Name}: {e.Message}",
                    LogEventLevel.Warning
                );
            }
            if (directories is null)
                return folders;

            await Parallel.ForEachAsync(
                directories,
                SystemParallelism.Options,
                async (directory, cancellationToken) =>
                {
                    string folderName = Path.GetFileName(directory);

                    if (
                        (_regexFilterEnabled && FolderNameRegex().IsMatch(folderName))
                        || depth == 0
                    )
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
                // A ".back" folder is the original kept during a re-encode, never a title.
                .Where(e =>
                    !e.TrimEnd('/', '\\').EndsWith(".back", StringComparison.OrdinalIgnoreCase)
                )
                .OrderBy(f => f);

            Parallel.ForEach(
                directories,
                SystemParallelism.Options,
                (directory, _) =>
                {
                    string dir = _driver.GetFullPath(directory.ToUtf8());
                    Logger.App($"Scanning {dir}");

                    string folderName = Path.GetFileName(dir);

                    if (_regexFilterEnabled && FolderNameRegex().IsMatch(folderName))
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

    /// <summary>What a scan reads from one file's name: title, year, season and episode.</summary>
    public MovieFile ParseFileName(string file, string libraryType) =>
        BuiltInFilenameParser
            .Resolver.Resolve(
                Path.GetFileName(file),
                Path.GetDirectoryName(file),
                file,
                libraryType
            )
            .Parsed;

    internal async Task<(FfProbeData? Probe, TagFile? Tags)> ProbeAsync(
        string file,
        bool isAudioFile,
        CancellationToken cancellationToken
    )
    {
        FfProbeData? ffprobe = null;
        TagFile? tagFile = null;
        try
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
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Logger.App($"Probe of {file} failed: {e.GetType().Name}: {e}", LogEventLevel.Fatal);
        }

        return (ffprobe, tagFile);
    }

    private async Task<ConcurrentBag<MediaFile>> FilesAsync(string folderPath)
    {
        ConcurrentBag<MediaFile> files = [];
        try
        {
            // SMB's directory response already tells us which entries are files.
            // Reopening each name just to classify it is especially costly in
            // folders containing thousands of HLS subtitle segments.
            IEnumerable<string> entries = (
                _driver is SmbStorageDriver
                    ? _driver
                        .EnumerateEntries(folderPath, "*", SearchOption.TopDirectoryOnly)
                        .Where(entry => !entry.IsDirectory)
                        .Select(entry => entry.Path)
                    : _driver.EnumerateFileSystemEntries(
                        folderPath,
                        "*",
                        SearchOption.TopDirectoryOnly
                    )
            ).Where(entry =>
                _extensionFilter.Length == 0
                || _extensionFilter.Contains(Path.GetExtension(entry).ToLowerInvariant())
            );
            if (_driver is not SmbStorageDriver)
                entries = entries.Where(_driver.FileExists);

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

                    bool isVideoFile = _videoExtensions.Contains(extension);
                    bool isAudioFile = _audioExtensions.Contains(extension);
                    bool isSubtitleFile = extension is ".srt" or ".vtt" or ".ass" or ".sub";

                    if (!isVideoFile && !isAudioFile && !isSubtitleFile)
                        return;

                    MovieFile? movieFile =
                        isVideoFile ? ParseFileName(file, _libraryType)
                        : isAudioFile ? _movieDetector.GetInfo(file)
                        : null;

                    (FfProbeData? ffprobe, TagFile? tagFile) =
                        isVideoFile || isAudioFile
                            ? await ProbeAsync(file, isAudioFile, cancellationToken)
                            : (null, null);

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
            Logger.App(
                $"Listing files in {folderPath} stopped early: {e.GetType().Name}: {e.Message}",
                LogEventLevel.Fatal
            );

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
