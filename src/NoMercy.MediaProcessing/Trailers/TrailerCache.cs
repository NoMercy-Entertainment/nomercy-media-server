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

    // YouTube video ids are exactly 11 chars of [A-Za-z0-9_-]. The id flows into
    // shell command strings (yt-dlp/ffmpeg) and filesystem paths, so a strict match
    // is the trust boundary that blocks command injection and path traversal.
    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
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

        string workingDirectory = Path.Combine(AppFiles.TranscodePath, trailerId);
        _ = Task.Run(() => Download(trailerId, language, workingDirectory), ct);

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

    private void Download(string trailerId, string language, string workingDirectory)
    {
        try
        {
            string command = TrailerCommandBuilder.Build(
                AppFiles.YtdlpPath,
                AppFiles.FfmpegPath,
                trailerId,
                language
            );

            if (Software.IsWindows)
            {
                logger.LogDebug("cmd -c \"{Command}\"", command);
                Shell.ExecSync(
                    "cmd",
                    $"/c \"{command}\"",
                    new() { WorkingDirectory = workingDirectory }
                );
            }
            else
            {
                logger.LogDebug("/bin/bash -c \"{Command}\"", command);
                Shell.ExecSync(
                    "/bin/bash",
                    $"-c \"{command}\"",
                    new() { WorkingDirectory = workingDirectory }
                );
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                "Trailer download failed for {TrailerId}: {Message}",
                trailerId,
                ex.Message
            );
        }
    }
}
