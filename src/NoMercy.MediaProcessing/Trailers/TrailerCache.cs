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
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Trailers;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.NewtonSoftConverters;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.Trailers;

/// <param name="transcodeStorage">Storage scoped to the transcode folder.</param>
public partial class TrailerCache(IStorage transcodeStorage, ILogger<TrailerCache> logger)
    : ITrailerCache
{
    private const string InfoFileName = "info.json";
    private const string FirstSegmentFileName = "video_00002.ts";
    private static readonly TimeSpan FirstSegmentTimeout = TimeSpan.FromSeconds(30);

    // YouTube video ids are exactly 11 chars of [A-Za-z0-9_-]. The id also
    // reaches filesystem paths, so keep this guard at the download sink.
    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TrailerIdRegex();

    public static bool IsValidId(string trailerId) => TrailerIdRegex().IsMatch(trailerId);

    public async Task<bool> FetchInfoAsync(string trailerId, CancellationToken ct = default)
    {
        string infoJsonPath = transcodeStorage.CombinePath(trailerId, InfoFileName);

        if (await transcodeStorage.ExistsAsync(infoJsonPath, ct))
        {
            string text = await transcodeStorage.ReadAllTextAsync(infoJsonPath, ct);
            if (text.FromJson<TrailerInfo>() is not null)
                return true;
        }

        Shell.ExecResult result = await Shell.ExecAsync(
            AppFiles.YtdlpPath,
            [
                "-f",
                "bestvideo+bestaudio",
                "-j",
                $"https://youtube.com/watch?v={trailerId}",
                "--extractor-args",
                "youtube:player_client=default",
            ]
        );

        if (!result.Success || string.IsNullOrEmpty(result.StandardOutput))
        {
            logger.LogError(result.StandardError);
            return false;
        }

        if (!await transcodeStorage.ExistsAsync(trailerId, ct))
            await transcodeStorage.CreateDirectoryAsync(trailerId, ct);

        await transcodeStorage.WriteAllTextAsync(infoJsonPath, result.StandardOutput, ct);
        return true;
    }

    public async Task<TrailerInfo?> ReadInfoAsync(string trailerId, CancellationToken ct = default)
    {
        if (!await transcodeStorage.ExistsAsync(trailerId, ct))
            await transcodeStorage.CreateDirectoryAsync(trailerId, ct);

        string infoJsonPath = transcodeStorage.CombinePath(trailerId, InfoFileName);
        string text = await transcodeStorage.ReadAllTextAsync(infoJsonPath, ct);
        return text.FromJson<TrailerInfo>();
    }

    public async Task EnsureSegmentsAsync(
        string trailerId,
        string language,
        CancellationToken ct = default
    )
    {
        string firstSegmentPath = transcodeStorage.CombinePath(trailerId, FirstSegmentFileName);
        if (await transcodeStorage.ExistsAsync(firstSegmentPath, ct))
            return;

        _ = Task.Run(() => DownloadAsync(trailerId, language), ct);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(FirstSegmentTimeout);
        while (!await transcodeStorage.ExistsAsync(firstSegmentPath, ct))
            await Task.Delay(1000, timeout.Token);
    }

    public async Task<bool> RemoveAsync(string trailerId, CancellationToken ct = default)
    {
        if (!await transcodeStorage.ExistsAsync(trailerId, ct))
            return true;

        string trailerAbsPath = Path.Combine(AppFiles.TranscodePath, trailerId);
        try
        {
            await transcodeStorage.DeleteDirectoryAsync(trailerId, recursive: true, ct: ct);
            logger.LogInformation("Trailer folder deleted: {TrailerAbsPath}", trailerAbsPath);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(
                "Failed to delete trailer folder {TrailerAbsPath}: {Message}",
                trailerAbsPath,
                ex.Message
            );
            return false;
        }
    }

    internal static (ProcessStartInfo YtDlp, ProcessStartInfo Ffmpeg) CreateDownloadStartInfo(
        string ytdlpPath,
        string ffmpegPath,
        string workingDirectory,
        string trailerId,
        string? language
    )
    {
        ProcessStartInfo ytdlp = new()
        {
            FileName = ytdlpPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (
            string argument in new[]
            {
                "-f",
                "bestvideo+bestaudio",
                "--extractor-args",
                "youtube:player_client=default",
            }
        )
            ytdlp.ArgumentList.Add(argument);

        if (TrailerCommandBuilder.IsSafeLanguage(language))
        {
            ytdlp.ArgumentList.Add("-o");
            ytdlp.ArgumentList.Add($"subtitle:{language}.%(ext)s");
            ytdlp.ArgumentList.Add("--sub-langs");
            ytdlp.ArgumentList.Add("all");
            ytdlp.ArgumentList.Add("--write-subs");
        }

        ytdlp.ArgumentList.Add($"https://youtube.com/watch?v={trailerId}");
        ytdlp.ArgumentList.Add("-o");
        ytdlp.ArgumentList.Add("-");

        ProcessStartInfo ffmpeg = new()
        {
            FileName = ffmpegPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (
            string argument in new[]
            {
                "-i",
                "pipe:",
                "-map",
                "0:0",
                "-map",
                "0:1",
                "-c:v",
                "libx264",
                "-c:a",
                "aac",
                "-ac",
                "2",
                "-preset",
                "ultrafast",
                "-segment_list_type",
                "m3u8",
                "-hls_playlist_type",
                "event",
                "-hls_init_time",
                "4",
                "-hls_time",
                "4",
                "-hls_segment_filename",
                "video_%05d.ts",
                "video.m3u8",
            }
        )
            ffmpeg.ArgumentList.Add(argument);

        return (ytdlp, ffmpeg);
    }

    private async Task DownloadAsync(string trailerId, string language)
    {
        if (!IsValidId(trailerId))
        {
            logger.LogError("Refused a trailer download for a malformed id");
            return;
        }

        string workingDirectory = Path.Combine(AppFiles.TranscodePath, trailerId);

        (ProcessStartInfo ytdlpInfo, ProcessStartInfo ffmpegInfo) = CreateDownloadStartInfo(
            AppFiles.YtdlpPath,
            AppFiles.FfmpegPath,
            workingDirectory,
            trailerId,
            language
        );
        using Process ytdlp = new() { StartInfo = ytdlpInfo };
        using Process ffmpeg = new() { StartInfo = ffmpegInfo };
        bool ytdlpStarted = false;
        bool ffmpegStarted = false;

        try
        {
            ffmpeg.Start();
            ffmpegStarted = true;
            Shell.ChildProcessManager.Attach(ffmpeg);
            ytdlp.Start();
            ytdlpStarted = true;
            Shell.ChildProcessManager.Attach(ytdlp);

            Task<string> ytdlpError = ytdlp.StandardError.ReadToEndAsync();
            Task<string> ffmpegError = ffmpeg.StandardError.ReadToEndAsync();
            Task ffmpegOutput = ffmpeg.StandardOutput.BaseStream.CopyToAsync(Stream.Null);

            try
            {
                await ytdlp.StandardOutput.BaseStream.CopyToAsync(ffmpeg.StandardInput.BaseStream);
            }
            finally
            {
                ffmpeg.StandardInput.Close();
            }

            await Task.WhenAll(
                ytdlp.WaitForExitAsync(),
                ffmpeg.WaitForExitAsync(),
                ffmpegOutput,
                ytdlpError,
                ffmpegError
            );
            if (ytdlp.ExitCode != 0 || ffmpeg.ExitCode != 0)
                logger.LogError(
                    "Trailer download failed for {TrailerId}: yt-dlp ({YtDlpExitCode}) {YtDlpError}; ffmpeg ({FfmpegExitCode}) {FfmpegError}",
                    trailerId,
                    ytdlp.ExitCode,
                    await ytdlpError,
                    ffmpeg.ExitCode,
                    await ffmpegError
                );
        }
        catch (Exception ex)
        {
            logger.LogError(
                "Trailer download failed for {TrailerId}: {Message}",
                trailerId,
                ex.Message
            );
        }
        finally
        {
            KillIfRunning(ytdlp, ytdlpStarted);
            KillIfRunning(ffmpeg, ffmpegStarted);
        }
    }

    private static void KillIfRunning(Process process, bool started)
    {
        if (!started)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
