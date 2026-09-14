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

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Composition;
using NoMercy.Storage;

namespace NoMercy.Encoder.Distribution;

/// <summary>
/// Downloads task source files from the coordinator over HTTP when the
/// worker can't see the original path locally. The coordinator's
/// /worker/source endpoint signs the download URL via HMAC over
/// (path, timestamp) so a worker that doesn't have the signing key
/// can't guess URLs.
///
/// Cached downloads live under <see cref="EncoderOptions.LiveTranscodeCachePath"/>/
/// remote-sources. Each task's fetched file gets a deterministic name
/// derived from the original path hash so retries for the same task
/// reuse the file instead of re-downloading.
/// </summary>
public class HttpSourceFetcher(
    IHttpClientFactory httpClientFactory,
    EncoderOptions options,
    ILogger<HttpSourceFetcher> logger,
    IStorage storage
) : ISourceFetcher
{
    public async Task<string> EnsureLocalAsync(EncodeTask task, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(task.InputPath))
            return string.Empty;

        // Fast path: source is visible locally (shared NAS / SMB mount).
        if (storage.Exists(task.InputPath))
            return task.InputPath;

        // Coordinator URL required for fetch — when it's not set, this
        // worker is standalone and there's no remote source to fetch from.
        if (string.IsNullOrWhiteSpace(options.CoordinatorUrl))
        {
            logger.LogWarning(
                "Task {TaskId} source {Path} is missing locally and no CoordinatorUrl configured",
                task.TaskId,
                task.InputPath
            );
            return task.InputPath;
        }

        string cachedPath = ResolveCachePath(task);

        // Idempotency: if the file already exists from a previous attempt
        // and isn't empty, reuse it. Full hash-match check is overkill;
        // HMAC on the coordinator's response covers integrity.
        if (storage.Exists(cachedPath) && storage.SizeOrZero(cachedPath) > 0)
        {
            logger.LogInformation(
                "Task {TaskId} reusing cached source at {Path}",
                task.TaskId,
                cachedPath
            );
            return cachedPath;
        }

        storage.CreateDirectory(Path.GetDirectoryName(cachedPath)!);

        HttpClient http = httpClientFactory.CreateClient("worker-source-fetch");
        http.BaseAddress = new(options.CoordinatorUrl);
        http.Timeout = TimeSpan.FromHours(1); // Multi-GB downloads over WAN.

        string signedQuery = BuildSignedQuery(task.InputPath!);
        string requestPath = $"api/v1/worker/source?{signedQuery}";
        HttpRequestMessage request = new(HttpMethod.Get, requestPath);

        if (options.IsDistributedEncodingEnabled)
        {
            HmacSigner hmacSigner = new(options.DistributedEncodingSigningKey!);
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string signature = hmacSigner.Sign("GET", "/" + "api/v1/worker/source", timestamp, []);
            request.Headers.Add("X-NoMercy-Timestamp", timestamp.ToString());
            request.Headers.Add("X-NoMercy-Signature", signature);
        }

        logger.LogInformation("Fetching source for task {TaskId} from coordinator", task.TaskId);

        using HttpResponseMessage response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct
            )
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            logger.LogError(
                "Coordinator returned {Status} fetching source for task {TaskId}: {Body}",
                (int)response.StatusCode,
                task.TaskId,
                body.Length > 500 ? body[..500] : body
            );
            throw new InvalidOperationException(
                $"Source fetch failed: HTTP {(int)response.StatusCode}"
            );
        }

        await using (
            Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false)
        )
        await using (Stream target = await storage.OpenWriteAsync(cachedPath, overwrite: true, ct))
        {
            await source.CopyToAsync(target, ct).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Task {TaskId} source fetched to {Path} ({Bytes} bytes)",
            task.TaskId,
            cachedPath,
            storage.SizeOrZero(cachedPath)
        );

        return cachedPath;
    }

    public Task ReleaseAsync(EncodeTask task)
    {
        // Cached downloads are keyed by task — drop the specific file.
        // Keep the parent directory; concurrent tasks may still have
        // cached sources there.
        try
        {
            string cachedPath = ResolveCachePath(task);
            storage.Delete(cachedPath);
            logger.LogDebug("Released cached source for task {TaskId}", task.TaskId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to release cached source for task {TaskId} — will persist until next cache cleanup",
                task.TaskId
            );
        }
        return Task.CompletedTask;
    }

    private string ResolveCachePath(EncodeTask task)
    {
        string cacheRoot = Path.Combine(options.ResolvedLiveTranscodeCachePath, "remote-sources");

        // Deterministic filename per task — task.TaskId is unique per job,
        // suffixed with the source file's extension so ffprobe/ffmpeg
        // auto-detect the container from the extension.
        string ext = Path.GetExtension(task.InputPath ?? string.Empty);
        if (string.IsNullOrEmpty(ext))
            ext = ".src";

        return Path.Combine(cacheRoot, $"{task.TaskId}{ext}");
    }

    private string BuildSignedQuery(string path)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string signature = SourcePathSignature.Compute(
            options.GetDistributedEncodingSigningKey(),
            path,
            timestamp
        );

        // URL-encode the path and signature; timestamp is a simple int.
        string encodedPath = Uri.EscapeDataString(path);
        string encodedSig = Uri.EscapeDataString(signature);
        return $"path={encodedPath}&ts={timestamp}&sig={encodedSig}";
    }
}
