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

using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using MimeMapping;
using NoMercy.NmSystem.Monitoring;
using NoMercy.Storage;

namespace NoMercy.Api.Middleware;

public class DynamicStaticFilesMiddleware(
    RequestDelegate next,
    IServedFolderRegistry folders,
    ILogger<DynamicStaticFilesMiddleware> logger
)
{
    // Define streamable media file extensions
    private static readonly HashSet<string> StreamableExtensions = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ".mp4",
        ".mkv",
        ".avi",
        ".mov",
        ".wmv",
        ".flv",
        ".webm",
        ".m4v",
        ".3gp",
        ".ogv",
        ".mp3",
        ".aac",
        ".flac",
        ".ogg",
        ".wav",
        ".wma",
        ".m4a",
        ".opus",
    };

    public async Task InvokeAsync(
        HttpContext context,
        IStorageFactory storageFactory,
        MediaActivityMonitor activityMonitor
    )
    {
        if (!TryParseFolderId(context.Request.Path, out Ulid folderId))
        {
            await next(context);
            return;
        }

        try
        {
            if (!folders.TryGet(folderId, out FolderRef folderRef))
            {
                logger.LogInformation(
                    "[DynamicStaticFiles] folder {FolderId} not registered (request: {Path})",
                    folderId,
                    context.Request.Path
                );
                await next(context);
                return;
            }

            string relativeWithinFolder = RelativeWithinFolder(context.Request.Path.Value);

            // Per-request server-side timing for media serves. Audio/video file
            // requests bypass AccessLogMiddleware, so without this they have zero
            // timing visibility. Logs how long the server itself spends resolving
            // + opening + streaming the file — isolating "server slow" from
            // client-side connect/DNS/TLS latency.
            Stopwatch stopwatch = Stopwatch.StartNew();
            long resolvedAtMs = 0;

            IStorage? storage = OpenExistingFile(
                storageFactory,
                folderId,
                folderRef,
                relativeWithinFolder
            );
            if (storage is null)
            {
                await next(context);
                return;
            }

            Uri? presigned = await storage.TryGetPresignedUrlAsync(
                relativeWithinFolder,
                TimeSpan.FromHours(1),
                context.RequestAborted
            );
            if (presigned is not null)
            {
                context.Response.StatusCode = 302;
                context.Response.Headers.Location = presigned.ToString();
                return;
            }

            // Every playlist/segment/subtitle fetch counts as playback activity —
            // background NAS-heavy jobs (scans/imports/extras) defer while this
            // keeps landing, see MediaPlaybackActivityGate.
            activityMonitor.Touch();

            // Time-to-first-byte on the server: everything before the stream loop
            // (factory resolve + Exists + presigned probe + Size + OpenRead).
            resolvedAtMs = stopwatch.ElapsedMilliseconds;
            await ServeFile(context, storage, relativeWithinFolder);
            stopwatch.Stop();

            LogServeTiming(
                relativeWithinFolder,
                resolvedAtMs,
                stopwatch.ElapsedMilliseconds,
                storage
            );
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Race: file or its containing directory vanished between Exists()
            // and Size()/OpenRead(). Translate to 404 instead of an opaque 500.
            logger.LogWarning(
                "[DynamicStaticFiles] file vanished mid-serve for '{Path}': {Message}",
                context.Request.Path,
                ex.Message
            );
            if (!context.Response.HasStarted)
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            // Storage-layer transport failure (NFS hiccup, S3 / WebDAV 5xx, disk
            // error). 502 reflects "we couldn't reach the backend that holds
            // this file" — distinct from "the file doesn't exist."
            logger.LogWarning(
                "[DynamicStaticFiles] storage transport failure for '{Path}': {Message}",
                context.Request.Path,
                ex.Message
            );
            if (!context.Response.HasStarted)
                context.Response.StatusCode = (int)HttpStatusCode.BadGateway;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected mid-stream — no response to send.
        }
        catch (Exception ex)
        {
            // Anything else escaping ServeFile is treated as a backend fault,
            // not a server bug — surface as 502 so ExoPlayer / browser fall
            // through to the next track gracefully instead of crashing on a
            // 500 with a stack trace body. Logged at Error so genuine bugs
            // remain visible in the sink.
            logger.LogError(
                "[DynamicStaticFiles] unhandled exception for path '{Path}': {Ex}",
                context.Request.Path,
                ex
            );
            if (!context.Response.HasStarted)
                context.Response.StatusCode = (int)HttpStatusCode.BadGateway;
        }
    }

    // API endpoints, Swagger and other system paths are never folder routes.
    private static readonly string[] SystemRoots = ["api", "index.html", "images", "manage"];

    /// <summary>A request for a file inside a registered folder has the folder id as its first segment.</summary>
    internal static bool TryParseFolderId(PathString path, out Ulid folderId)
    {
        folderId = default;

        string[] pathSegments = path.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!path.HasValue || pathSegments.Length == 0)
            return false;

        string rootPath = pathSegments[0];
        if (
            SystemRoots.Any(root => rootPath.Equals(root, StringComparison.OrdinalIgnoreCase))
            || rootPath.StartsWith("swagger", StringComparison.OrdinalIgnoreCase)
        )
            return false;

        return Ulid.TryParse(rootPath, out folderId);
    }

    /// <summary>
    /// The file's path within its folder: the leading "/&lt;folderId&gt;" segment stripped,
    /// URL-decoded, so storage drivers see a consistent shape.
    /// </summary>
    private static string RelativeWithinFolder(string? pathValue) =>
        pathValue is null
            ? string.Empty
            : Uri.UnescapeDataString(pathValue[pathValue.IndexOf('/', 1)..]).TrimStart('/');

    /// <summary>
    /// The folder's storage when <paramref name="relativeWithinFolder"/> exists on it;
    /// null, logged, when the storage cannot be opened or the file is not there.
    /// </summary>
    private IStorage? OpenExistingFile(
        IStorageFactory storageFactory,
        Ulid folderId,
        FolderRef folderRef,
        string relativeWithinFolder
    )
    {
        IStorage storage;
        try
        {
            storage = storageFactory.For(
                folderId: folderId,
                driverId: folderRef.DriverId,
                subPath: folderRef.SubPath
            );
        }
        catch (Exception fEx)
        {
            logger.LogInformation(
                "[DynamicStaticFiles] factory.For failed for folder {FolderId} driver {DriverId} subPath '{SubPath}': {Message}",
                folderId,
                folderRef.DriverId,
                folderRef.SubPath,
                fEx.Message
            );
            return null;
        }

        try
        {
            if (storage.Exists(relativeWithinFolder))
                return storage;
        }
        catch (Exception eEx)
        {
            logger.LogInformation(
                "[DynamicStaticFiles] storage.Exists threw on '{RelativeWithinFolder}' (folder {FolderId}, driver {DriverId}): {Message}",
                relativeWithinFolder,
                folderId,
                folderRef.DriverId,
                eEx.Message
            );
            return null;
        }

        logger.LogInformation(
            "[DynamicStaticFiles] not found: folder={FolderId} driver={DriverId} subPath='{SubPath}' relative='{RelativeWithinFolder}'",
            folderId,
            folderRef.DriverId,
            folderRef.SubPath,
            relativeWithinFolder
        );
        return null;
    }

    private void LogServeTiming(
        string relativeWithinFolder,
        long resolvedAtMs,
        long elapsedMs,
        IStorage storage
    )
    {
        if (resolvedAtMs > 1000 || elapsedMs > 2000)
            logger.LogWarning(
                "[DynamicStaticFiles] SLOW serve '{RelativeWithinFolder}' prep={ResolvedAtMs}ms total={ElapsedMilliseconds}ms (driver={Name})",
                relativeWithinFolder,
                resolvedAtMs,
                elapsedMs,
                storage.GetType().Name
            );
        else
            logger.LogDebug(
                "[DynamicStaticFiles] serve '{RelativeWithinFolder}' prep={ResolvedAtMs}ms total={ElapsedMilliseconds}ms",
                relativeWithinFolder,
                resolvedAtMs,
                elapsedMs
            );
    }

    private async Task ServeFile(HttpContext context, IStorage storage, string relativePath)
    {
        long fileLength = storage.Size(relativePath);

        // Surface storage-reported zero — empty bodies on m3u8 / vtt /
        // fonts.json requests almost always trace back to either an encoder
        // that hasn't flushed yet or an NFS metadata cache lying. Logging it
        // here narrows triage in one step instead of guessing.
        if (fileLength == 0)
        {
            logger.LogWarning(
                "[DynamicStaticFiles] storage reports 0 bytes for '{Path}' (driver={Name})",
                context.Request.Path,
                storage.GetType().Name
            );
        }

        context.Response.ContentType = ResolveContentType(relativePath);

        // Tell ResponseCachingMiddleware not to wrap the body. Without this
        // header it still allocates the cache stream wrapper around every
        // FLAC / video chunk we write — pointless overhead since media
        // responses are too large to ever cache (cap is 64 MB by default).
        context.Response.Headers.CacheControl = "no-store";

        bool hasRangeRequest = context.Request.Headers.TryGetValue(
            "Range",
            out StringValues rangeValue
        );

        // A request that carries no Range asks for the whole representation, and
        // only a 200 may answer it. Replying 206 with a probe chunk truncates the
        // file for anything that fetches without a Range — a CDN filling its cache
        // does exactly that, then serves that first megabyte back for every range
        // the player asks for afterwards, so playback never advances past it.
        // Browsers open media with `bytes=0-` regardless, so the probe-chunk
        // fast start below still applies where it was meant to.
        if (!hasRangeRequest)
        {
            context.Response.Headers.AcceptRanges = "bytes";
            context.Response.ContentLength = fileLength;
            await using Stream wholeStream = storage.OpenRead(relativePath);
            await wholeStream.CopyToAsync(context.Response.Body);
            return;
        }

        if (
            !TryResolveRange(
                rangeValue.ToString(),
                fileLength,
                IsStreamableMedia(relativePath),
                out long start,
                out long end
            )
        )
        {
            context.Response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
            context.Response.Headers.ContentRange = new ContentRangeHeaderValue(
                fileLength
            ).ToString();
            return;
        }

        long length = end - start + 1;

        context.Response.StatusCode = (int)HttpStatusCode.PartialContent;
        context.Response.Headers.ContentRange = new ContentRangeHeaderValue(
            start,
            end,
            fileLength
        ).ToString();
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.ContentLength = length;

        await using Stream fs = storage.OpenRead(relativePath);
        await CopyRangeAsync(fs, context, start, length);
    }

    /// <summary>
    /// The byte range to answer a <c>Range</c> header with, clamped to the file;
    /// false when the range cannot be satisfied.
    /// </summary>
    /// <remarks>
    /// An open-ended <c>bytes=0-</c> on streamable media gets only the first 1 MB, so a
    /// browser can parse the moov atom without waiting on the whole file. Any other
    /// open-ended range (start &gt; 0) is served to EOF: ExoPlayer's DefaultExtractorInput
    /// reads sequentially via Mp4Extractor.readFully, and capping at 1 MiB makes its read
    /// return -1 mid-atom and throws EOFException (web's &lt;video&gt; reopens the
    /// connection automatically; ExoPlayer does not). A range that starts past EOF or on a
    /// zero-length file is rejected here, because ContentRangeHeaderValue throws on
    /// start&lt;0 or start&gt;end and the player retried the resulting 500 in a tight loop.
    /// </remarks>
    internal static bool TryResolveRange(
        string rangeHeader,
        long fileLength,
        bool isStreamableMedia,
        out long start,
        out long end
    )
    {
        const long initialProbeChunkSize = 1024 * 1024;

        string[] ranges = rangeHeader.Replace("bytes=", "").Split('-');
        end = fileLength - 1;

        if (!long.TryParse(ranges[0], out start))
            return false;

        if (ranges.Length > 1 && !string.IsNullOrEmpty(ranges[1]))
        {
            if (!long.TryParse(ranges[1], out end))
                return false;
        }
        else if (isStreamableMedia && start == 0)
        {
            end = Math.Min(initialProbeChunkSize - 1, fileLength - 1);
        }

        if (end > fileLength - 1)
            end = fileLength - 1;

        return start >= 0 && start <= end;
    }

    private static async Task CopyRangeAsync(
        Stream fs,
        HttpContext context,
        long start,
        long length
    )
    {
        fs.Seek(start, SeekOrigin.Begin);
        byte[] buffer = new byte[64 * 1024];
        int bytesRead;
        long bytesToRead = length;

        while (bytesToRead > 0)
        {
            bytesRead = await fs.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, bytesToRead)),
                context.RequestAborted
            );
            if (bytesRead == 0)
                break;

            await context.Response.Body.WriteAsync(
                buffer.AsMemory(0, bytesRead),
                context.RequestAborted
            );
            bytesToRead -= bytesRead;
        }
    }

    private static bool IsStreamableMedia(string filePath)
    {
        string extension = Path.GetExtension(filePath);
        return StreamableExtensions.Contains(extension);
    }

    // MimeMapping (the NuGet package) doesn't know about subtitle/font/HLS
    // formats and defaults them to application/octet-stream — which the
    // browser refuses to render as text. Override the handful that matter
    // and fall back to the library for everything else.
    private static readonly Dictionary<string, string> ContentTypeOverrides = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        // Subtitles
        [".ass"] = "text/x-ssa; charset=utf-8",
        [".ssa"] = "text/x-ssa; charset=utf-8",
        [".srt"] = "application/x-subrip; charset=utf-8",
        [".vtt"] = "text/vtt; charset=utf-8",
        [".sub"] = "text/plain; charset=utf-8",
        [".idx"] = "text/plain; charset=utf-8",
        [".sup"] = "application/octet-stream",
        // HLS
        [".m3u8"] = "application/vnd.apple.mpegurl",
        [".m3u"] = "application/vnd.apple.mpegurl",
        [".ts"] = "video/mp2t",
        // Fonts (encoder-extracted attachments)
        [".otf"] = "font/otf",
        [".ttf"] = "font/ttf",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
    };

    private static string ResolveContentType(string filePath)
    {
        string ext = Path.GetExtension(filePath);
        if (ContentTypeOverrides.TryGetValue(ext, out string? mapped))
            return mapped;
        return MimeUtility.GetMimeMapping(filePath);
    }
}
