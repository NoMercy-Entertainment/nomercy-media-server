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

using System.Drawing;
using ImageMagick;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Providers.Helpers;
using NoMercy.Storage;
using Serilog.Events;

namespace NoMercy.Providers.NoMercy.Client;

public abstract class NoMercyImageClient
{
    // Image downloads use their own queue rather than the shared TMDB API queue.
    private static readonly Queue ImageQueue = new(
        new()
        {
            Concurrent = 50,
            Interval = 1000,
            Start = true,
        }
    );

    private static IStorage? _storage;

    public static void Initialize(IStorage storage)
    {
        _storage = storage;
    }

    private static IStorage Storage =>
        _storage
        ?? throw new InvalidOperationException(
            "NoMercyImageClient has not been initialized. Call NoMercyImageClient.Initialize() at startup."
        );

    public static Task<MagickImage?> Download(
        string? path,
        bool? download = true,
        Size? maxDecodeSize = null
    )
    {
        return ImageQueue.Enqueue(Task, $"original{path}", true);

        async Task<MagickImage?> Task()
        {
            if (path is null)
                return null;

            try
            {
                string folder = Path.Join(AppFiles.ImagesPath, "original");

                IStorage storage = Storage;
                await storage.CreateDirectoryAsync(folder, CancellationToken.None);

                string filePath = Path.Combine(folder, path.Replace("/", "").Replace("\\", ""));

                if (await storage.ExistsAsync(filePath, CancellationToken.None))
                    return new(filePath, MagickReadSettingsFactory.Create(maxDecodeSize));

                HttpClient httpClient = HttpClientProvider.CreateClient(
                    HttpClientNames.NoMercyImage
                );

                string url = path.StartsWith("http") ? path : $"original{path}";

                using HttpResponseMessage response = await httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                    return null;

                byte[] bytes = await response.Content.ReadAsByteArrayAsync();

                if (
                    download is not false
                    && !await storage.ExistsAsync(filePath, CancellationToken.None)
                )
                    await storage.WriteAsync(filePath, bytes, CancellationToken.None);

                return new(bytes, MagickReadSettingsFactory.Create(maxDecodeSize));
            }
            catch (Exception e)
            {
                Logger.MovieDb(
                    $"Error downloading image: {path} - {e.Message}",
                    LogEventLevel.Error
                );
            }

            return null;
        }
    }
}
