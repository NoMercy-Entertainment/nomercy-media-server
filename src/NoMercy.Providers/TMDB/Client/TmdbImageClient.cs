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

using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Providers.Helpers;
using NoMercy.Storage;
using Serilog.Events;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using Image = SixLabors.ImageSharp.Image;

namespace NoMercy.Providers.TMDB.Client;

public abstract class TmdbImageClient
{
    public const string ImageBaseUrl = "https://image.tmdb.org/t/p/";

    // Image downloads hit image.tmdb.org (a separate host from the API) and
    // are throttled by their own queue rather than the shared API queue.
    private static Queue ImageQueue => ProviderQueues.For(HttpClientNames.TmdbImage);

    private static IStorage? _storage;

    public static void Initialize(IStorage storage)
    {
        _storage = storage;
    }

    private static IStorage Storage =>
        _storage
        ?? throw new InvalidOperationException(
            "TmdbImageClient has not been initialized. Call TmdbImageClient.Initialize() at startup."
        );

    public static Task<Image<Rgba32>?>? Download(
        string? path,
        bool? download = true,
        Size? maxDecodeSize = null
    )
    {
        try
        {
            return DownloadQueuedAsync();
        }
        catch (InvalidImageContentException e)
        {
            Logger.MovieDb(
                $"Image format error downloading image: {path} - {e.Message}",
                LogEventLevel.Error
            );
            return null;
        }
        catch (ImageFormatException e)
        {
            Logger.MovieDb(
                $"Image format error downloading image: {path} - {e.Message}",
                LogEventLevel.Error
            );
            return null;
        }

        async Task<Image<Rgba32>?> DownloadQueuedAsync()
        {
            try
            {
                return await ImageQueue.Enqueue(Task, path, true);
            }
            catch (HttpRequestException ex)
                when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    || (int?)ex.StatusCode is >= 500 and <= 599
                )
            {
                return null;
            }
        }

        async Task<Image<Rgba32>?> Task()
        {
            try
            {
                if (path is null)
                    return null;

                // A null/empty image path (an entity with no poster/backdrop) must
                // never reach the write below: path.Replace("/", "") collapses to
                // an empty file name, so filePath becomes the 'original' folder
                // itself and WriteAsync targets the directory — "Access to the path
                // '…/cache/images/original' is denied". Nothing to download here.
                string fileName = path.Replace("/", "");
                if (string.IsNullOrWhiteSpace(fileName))
                    return null;

                bool isSvg = path.EndsWith(".svg");
                string folder = Path.Join(AppFiles.ImagesPath, "original");

                IStorage storage = Storage;
                await storage.CreateDirectoryAsync(folder, CancellationToken.None);

                string filePath = Path.Join(folder, fileName);
                if (await storage.ExistsAsync(filePath, CancellationToken.None))
                {
                    if (isSvg)
                        return null;

                    try
                    {
                        if (maxDecodeSize.HasValue)
                        {
                            DecoderOptions options = new() { TargetSize = maxDecodeSize.Value };
                            return await Image.LoadAsync<Rgba32>(options, filePath);
                        }

                        return await Image.LoadAsync<Rgba32>(filePath);
                    }
                    catch (Exception e)
                        when (e is ImageFormatException or InvalidImageContentException)
                    {
                        // A poisoned cache entry (a non-image body persisted by an
                        // older build, or a truncated write). Delete it so this run
                        // re-downloads a clean copy instead of failing on every
                        // scan forever.
                        Logger.MovieDb(
                            $"Discarding undecodable cached image, re-downloading: {path} - {e.Message}",
                            LogEventLevel.Warning
                        );
                        await storage.DeleteAsync(filePath, CancellationToken.None);
                    }
                }

                HttpClient httpClient = HttpClientProvider.CreateClient(HttpClientNames.TmdbImage);

                string url = path.StartsWith("http") ? path : $"original{path}";
                using HttpResponseMessage response = await httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    if (
                        response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                        || (int)response.StatusCode >= 500
                    )
                        response.EnsureProviderSuccess();
                    return null;
                }

                if (download is false)
                {
                    if (isSvg)
                        return null;

                    await using Stream contentStream = await response.Content.ReadAsStreamAsync();

                    if (maxDecodeSize.HasValue)
                    {
                        DecoderOptions options = new() { TargetSize = maxDecodeSize.Value };
                        return Image.Load<Rgba32>(options, contentStream);
                    }

                    return Image.Load<Rgba32>(contentStream);
                }

                byte[] bytes = await response.Content.ReadAsByteArrayAsync();

                if (isSvg)
                {
                    // SVG is not decoded (ImageSharp has no SVG decoder), but it
                    // is a valid asset — cache it as-is.
                    if (!await storage.ExistsAsync(filePath, CancellationToken.None))
                        await storage.WriteAsync(filePath, bytes, CancellationToken.None);
                    return null;
                }

                DecoderOptions? decoderOptions = maxDecodeSize.HasValue
                    ? new() { TargetSize = maxDecodeSize.Value }
                    : null;

                try
                {
                    // Validate the downloaded bytes decode as an image BEFORE
                    // persisting. A 200 response can still carry a non-image body
                    // (an HTML error page, a truncated CDN response); writing it
                    // first poisons the cache — the bad file then satisfies the
                    // Exists check on every later run and the decode fails
                    // forever. This probe image is disposed immediately; only a
                    // validated download reaches disk, so a transient bad
                    // download can be re-fetched cleanly next time.
                    if (decoderOptions is not null)
                    {
                        using Image<Rgba32> probe = Image.Load<Rgba32>(decoderOptions, bytes);
                    }
                    else
                    {
                        using Image<Rgba32> probe = Image.Load<Rgba32>(bytes);
                    }

                    if (!await storage.ExistsAsync(filePath, CancellationToken.None))
                        await storage.WriteAsync(filePath, bytes, CancellationToken.None);
                }
                catch (Exception e)
                {
                    Logger.MovieDb(
                        $"Error loading image: {path} - {e.Message}",
                        LogEventLevel.Error
                    );
                    return null;
                }

                // Ownership transfers to the caller (who disposes it); load from
                // the now-validated bytes.
                if (decoderOptions is not null)
                    return Image.Load<Rgba32>(decoderOptions, bytes);

                return Image.Load<Rgba32>(bytes);
            }
            catch (InvalidImageContentException e)
            {
                Logger.MovieDb(
                    $"Image format error downloading image: {path} - {e.Message}",
                    LogEventLevel.Error
                );
                return null;
            }
            catch (ImageFormatException e)
            {
                Logger.MovieDb(
                    $"Image format error downloading image: {path} - {e.Message}",
                    LogEventLevel.Error
                );
                return null;
            }
        }
    }
}
