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
using System.Text;
using System.Text.RegularExpressions;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Service;

public static class Dev
{
    public static async Task Run()
    {
        // string path = "M:\\Anime\\Anime";
        // string[] showFolders = Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly);
        //
        // foreach (string showFolder in showFolders)
        // {
        //     Logger.App($"Processing show folder: {showFolder}");
        //     string[] episodeFolders = Directory.GetDirectories(showFolder, "*", SearchOption.TopDirectoryOnly);
        //     foreach (string folder in episodeFolders)
        //     {
        //         await DeleteEmptyPlaylists(folder);
        //     }
        // }

        // await using MediaContext context = new();

        // List<Tv> shows = await context
        //     .Tvs
        //     // .Where(tv => tv.Library.Type == "tv")
        //     .Include(tv => tv.Episodes)
        //         .ThenInclude(episode => episode.VideoFiles)
        //             .ThenInclude(videoFile => videoFile.Metadata)
        //     // .Where(tv => tv.Id == 218613)
        //     .ToListAsync();

        // foreach (Tv show in shows)
        // foreach (Episode episode in show.Episodes)
        // {
        //     foreach (VideoFile videoFile in episode.VideoFiles)
        //     {
        //         if (videoFile.Metadata == null)
        //             continue;

        //         string hostFolder = videoFile.Metadata.HostFolder;
        //         if (string.IsNullOrEmpty(hostFolder))
        //             continue;

        // Logger.App($"Processing Episode: {episode.Title} (S{episode.SeasonNumber}E{episode.EpisodeNumber})");
        // Logger.App($"Host Folder: {hostFolder}");

        // DiagnoseMasterFolder(hostFolder);

        // await RecreateMasterPlaylist(hostFolder, videoFile.Filename);
        //     }
        // }

        // List<Movie> movies = await context
        //     .Movies.Where(tv => tv.Library.Type == MediaTypes.MovieMediaType)
        //     .Include(episode => episode.VideoFiles)
        //         .ThenInclude(videoFile => videoFile.Metadata)
        //     // .Where(tv => tv.Id == 60808)
        //     .ToListAsync();

        // foreach (Movie movie in movies)
        // {
        //     foreach (VideoFile videoFile in movie.VideoFiles)
        //     {
        //         if (videoFile.Metadata == null)
        //             continue;

        //         string hostFolder = videoFile.Metadata.HostFolder;
        //         if (string.IsNullOrEmpty(hostFolder))
        //             continue;

        // Logger.App($"Processing Movie: {movie.Title}");
        // Logger.App($"Host Folder: {hostFolder}");

        //DiagnoseMasterFolder(hostFolder);

        // await RecreateMasterPlaylist(hostFolder, videoFile.Filename);
        //     }
        // }

        await Task.CompletedTask;
    }

    private static IStorage CreateStorage()
    {
        IStorageDriver driver = new LocalStorageDriver();
        return new LocalStorage(driver, new([], driver));
    }

    private static Task DeleteEmptyPlaylists(string episodeFolder)
    {
        IStorage storage = CreateStorage();

        if (!storage.Exists(episodeFolder))
            return Task.CompletedTask;

        IEnumerable<StorageEntry> entries = storage.List(episodeFolder, "*.m3u8", false);
        IEnumerable<string> m3U8Files = entries.Select(e => e.Path);

        foreach (string playlistPath in m3U8Files)
        {
            string[] lines;
            try
            {
                lines = storage
                    .ReadAllTextAsync(playlistPath, CancellationToken.None)
                    .Result.Split(["\r\n", "\n"], StringSplitOptions.None);
            }
            catch
            {
                continue;
            }

            bool hasSegments = lines.Any(line =>
                !line.Trim().StartsWith("#") && !string.IsNullOrWhiteSpace(line)
            );
            if (!hasSegments)
            {
                try
                {
                    storage.Delete(playlistPath);
                    Logger.App($"Deleted empty playlist: {playlistPath}");
                }
                catch (Exception ex)
                {
                    Logger.App($"Failed to delete empty playlist {playlistPath}: {ex.Message}");
                }
            }
        }

        return Task.CompletedTask;
    }

    private static Dictionary<string, long> CalculateBitratesFromMaster(string episodeFolder)
    {
        Dictionary<string, long> results = new(StringComparer.OrdinalIgnoreCase);
        IStorage storage = CreateStorage();

        if (!storage.Exists(episodeFolder))
            return results;

        IEnumerable<string> m3U8Files = storage
            .List(episodeFolder, "*.m3u8", false)
            .Select(e => e.Path)
            .Where(f =>
            {
                try
                {
                    return storage
                        .ReadAllTextAsync(f, CancellationToken.None)
                        .Result.Contains("#EXT-X-STREAM-INF");
                }
                catch
                {
                    return false;
                }
            });

        foreach (string masterPath in m3U8Files)
        {
            string masterDir = Path.GetDirectoryName(masterPath) ?? episodeFolder;
            string[] lines;
            try
            {
                lines = storage
                    .ReadAllTextAsync(masterPath, CancellationToken.None)
                    .Result.Split(["\r\n", "\n"], StringSplitOptions.None);
            }
            catch
            {
                continue;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                if (
                    !lines[i]
                        .Trim()
                        .StartsWith(
                            "#EXT-X-STREAM-INF",
                            StringComparison.InvariantCultureIgnoreCase
                        )
                )
                    continue;

                // next non-comment line is the variant URI
                string? variantUri = null;
                for (int j = i + 1; j < lines.Length; j++)
                {
                    string nxt = lines[j].Trim();
                    if (string.IsNullOrEmpty(nxt) || nxt.StartsWith("#"))
                        continue;
                    variantUri = nxt;
                    break;
                }

                if (variantUri == null)
                    continue;
                if (Uri.IsWellFormedUriString(variantUri, UriKind.Absolute))
                    continue;

                string variantPath = Path.GetFullPath(Path.Combine(masterDir, variantUri));
                if (!storage.Exists(variantPath))
                    continue;

                long totalBytes = 0L;
                double totalSeconds = 0.0;

                string variantDir = Path.GetDirectoryName(variantPath) ?? masterDir;
                string[] vlines;
                try
                {
                    vlines = storage
                        .ReadAllTextAsync(variantPath, CancellationToken.None)
                        .Result.Split(["\r\n", "\n"], StringSplitOptions.None);
                }
                catch
                {
                    continue;
                }

                foreach (string raw in vlines)
                {
                    string vline = raw.Trim();
                    if (vline.StartsWith("#EXTINF:", StringComparison.InvariantCultureIgnoreCase))
                    {
                        string payload = vline.Substring("#EXTINF:".Length);
                        int comma = payload.IndexOf(',');
                        string durStr = comma >= 0 ? payload.Substring(0, comma) : payload;
                        if (
                            double.TryParse(
                                durStr,
                                NumberStyles.Any,
                                CultureInfo.InvariantCulture,
                                out double d
                            )
                        )
                            totalSeconds += d;

                        continue;
                    }

                    if (vline.StartsWith("#"))
                        continue;
                    string segRef = vline.Split(new[] { '?', '#' }, 2)[0];
                    if (string.IsNullOrWhiteSpace(segRef))
                        continue;
                    if (Uri.IsWellFormedUriString(segRef, UriKind.Absolute))
                        continue;

                    string segPath = Path.GetFullPath(Path.Combine(variantDir, segRef));
                    if (!storage.Exists(segPath))
                        continue;

                    try
                    {
                        long segSize = storage.Size(segPath);
                        totalBytes = checked(totalBytes + segSize);
                    }
                    catch (OverflowException)
                    {
                        // extremely unlikely; abort this variant
                        results[variantUri] = 0;
                        break;
                    }
                    catch
                    {
                        // ignore IO errors per-segment
                    }
                }

                if (totalBytes > 0 && totalSeconds > 0.0)
                {
                    double bits = totalBytes * 8.0;
                    long bitrate = (long)Math.Round(bits / totalSeconds);
                    results[variantUri] = bitrate;
                    Logger.App(
                        $"Computed bitrate for {variantUri}: {bitrate} bps (bytes={totalBytes}, seconds={totalSeconds:F2})"
                    );
                }
                else
                {
                    results[variantUri] = 0;
                    Logger.App(
                        $"Could not compute bitrate for {variantUri} (bytes={totalBytes}, seconds={totalSeconds:F2})"
                    );
                }
            }
        }

        return results;
    }

    // Diagnostic helper you can call locally to print a short report for an episode folder
    private static void DiagnoseMasterFolder(string hostFolder)
    {
        IStorage storage = CreateStorage();

        Logger.App($"Diagnosing folder: {hostFolder}");
        if (!storage.Exists(hostFolder))
        {
            Logger.App("Folder does not exist");
            return;
        }

        Dictionary<string, long> bitrates = CalculateBitratesFromMaster(hostFolder);
        if (bitrates.Count == 0)
        {
            Logger.App("No computed bitrates (no master playlists found or all remote/failed).\n");
            return;
        }

        foreach (KeyValuePair<string, long> kv in bitrates)
        {
            Logger.App($"Variant: {kv.Key} -> Bitrate: {kv.Value} bps");
        }

        // Optionally write a diagnostic file next to the master playlist(s)
        try
        {
            // Instead of writing a diagnostic JSON file, update the master playlists in-place
            IEnumerable<string> masters = storage
                .List(hostFolder, "*.m3u8", false)
                .Select(e => e.Path)
                .Where(f =>
                {
                    try
                    {
                        return storage
                            .ReadAllTextAsync(f, CancellationToken.None)
                            .Result.Contains("#EXT-X-STREAM-INF");
                    }
                    catch
                    {
                        return false;
                    }
                });

            Regex bwRegex = new(@"BANDWIDTH\s*=\s*\d+", RegexOptions.IgnoreCase);

            foreach (string masterPath in masters)
            {
                string original = storage
                    .ReadAllTextAsync(masterPath, CancellationToken.None)
                    .Result;
                string[] lines = original.Split(["\r\n", "\n"], StringSplitOptions.None);
                bool changed = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    if (
                        !lines[i]
                            .Trim()
                            .StartsWith(
                                "#EXT-X-STREAM-INF",
                                StringComparison.InvariantCultureIgnoreCase
                            )
                    )
                        continue;

                    // Find next non-empty, non-comment line for the variant URI
                    string? variantUri = null;
                    for (int j = i + 1; j < lines.Length; j++)
                    {
                        string nxt = lines[j].Trim();
                        if (string.IsNullOrEmpty(nxt) || nxt.StartsWith("#"))
                            continue;
                        variantUri = nxt;
                        break;
                    }

                    if (variantUri == null)
                        continue;
                    if (Uri.IsWellFormedUriString(variantUri, UriKind.Absolute))
                        continue;

                    if (!bitrates.TryGetValue(variantUri, out long computed))
                        continue;
                    if (computed <= 0)
                        continue;

                    string tag = lines[i];

                    // Replace or add BANDWIDTH attribute
                    if (bwRegex.IsMatch(tag))
                    {
                        tag = bwRegex.Replace(tag, $"BANDWIDTH={computed}");
                    }
                    else
                    {
                        // Ensure we append after the colon-separated attributes
                        tag += $",BANDWIDTH={computed}";
                    }

                    if (tag != lines[i])
                    {
                        lines[i] = tag;
                        changed = true;
                        Logger.App(
                            $"Updated playlist tag in {masterPath}: {variantUri} -> BANDWIDTH={computed}"
                        );
                    }
                }

                if (changed)
                {
                    try
                    {
                        storage
                            .WriteAllTextAsync(
                                masterPath,
                                string.Join(Environment.NewLine, lines),
                                CancellationToken.None
                            )
                            .GetAwaiter()
                            .GetResult();
                        Logger.App($"Wrote updated master playlist: {masterPath}");
                    }
                    catch (Exception ex)
                    {
                        Logger.App($"Failed updating master playlist {masterPath}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.App($"Failed updating playlists: {ex.Message}");
        }
    }

    private static async Task RecreateMasterPlaylist(string hostFolder, string filename)
    {
        if (string.IsNullOrEmpty(hostFolder))
            return;

        IStorage storage = CreateStorage();

        if (!storage.Exists(hostFolder))
        {
            Logger.App($"Host folder does not exist: {hostFolder}");
            return;
        }

        string targetName = Path.GetFileNameWithoutExtension(filename) ?? "master";

        try
        {
            // Find master playlists in the folder (those containing EXT-X-STREAM-INF)
            List<string> masters = storage
                .List(hostFolder, "*.m3u8", false)
                .Select(e => e.Path)
                .Where(f =>
                {
                    try
                    {
                        return storage
                            .ReadAllTextAsync(f, CancellationToken.None)
                            .Result.Contains("#EXT-X-STREAM-INF");
                    }
                    catch
                    {
                        return false;
                    }
                })
                .ToList();

            if (masters.Any())
            {
                string backupDir = Path.Combine(
                    hostFolder,
                    "_m3u8_backup_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss")
                );
                storage.CreateDirectory(backupDir);
                foreach (string m in masters)
                {
                    try
                    {
                        string dest = Path.Combine(backupDir, Path.GetFileName(m));
                        storage.Move(m, dest);
                        Logger.App($"Backed up master playlist {m} -> {dest}");
                    }
                    catch (Exception ex)
                    {
                        Logger.App($"Failed to backup {m}: {ex.Message}");
                    }
                }
            }

            // Build master playlist by scanning variant playlists in subdirectories
            StringBuilder masterBuilder = new();
            masterBuilder.AppendLine("#EXTM3U");

            // Video variants
            foreach (
                string dir in storage
                    .List(hostFolder, "video_*", false)
                    .Where(e => e.IsDirectory)
                    .Select(e => e.Path)
                    .OrderByDescending(d => d)
            )
            {
                IReadOnlyList<StorageEntry> dirPlaylists = storage.List(dir, "*.m3u8", false);
                if (dirPlaylists.Count == 0)
                    continue;

                string dirName = Path.GetFileName(dir);
                string relativePath = Path.Combine(dirName, Path.GetFileName(dirPlaylists[0].Path))
                    .Replace("\\", "/");

                // Parse resolution from directory name (video_1920x1080)
                Match resMatch = Regex.Match(dirName, @"video_(\d+)x(\d+)");
                if (resMatch.Success)
                {
                    string width = resMatch.Groups[1].Value;
                    string height = resMatch.Groups[2].Value;
                    masterBuilder.AppendLine(
                        $"#EXT-X-STREAM-INF:BANDWIDTH=8000000,RESOLUTION={width}x{height}"
                    );
                    masterBuilder.AppendLine(relativePath);
                }
            }

            // Audio variants
            foreach (
                string dir in storage
                    .List(hostFolder, "audio_*", false)
                    .Where(e => e.IsDirectory)
                    .Select(e => e.Path)
            )
            {
                IReadOnlyList<StorageEntry> dirPlaylists = storage.List(dir, "*.m3u8", false);
                if (dirPlaylists.Count == 0)
                    continue;

                string dirName = Path.GetFileName(dir);
                string relativePath = Path.Combine(dirName, Path.GetFileName(dirPlaylists[0].Path))
                    .Replace("\\", "/");
                masterBuilder.AppendLine(
                    $"#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"{dirName}\",URI=\"{relativePath}\""
                );
            }

            string newMaster = Path.Combine(hostFolder, targetName + ".m3u8");
            await storage.WriteAllTextAsync(
                newMaster,
                masterBuilder.ToString(),
                CancellationToken.None
            );
            Logger.App($"Recreated master playlist: {newMaster}");
        }
        catch (Exception ex)
        {
            Logger.App($"Failed recreating master playlist in {hostFolder}: {ex.Message}");
        }
    }
}
