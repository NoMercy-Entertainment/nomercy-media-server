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
using NoMercy.Providers.CoverArt.Models;
using NoMercy.Providers.Helpers;
using NoMercy.Storage;

namespace NoMercy.Providers.FanArt.Client;

public class FanArtImageClient : FanArtBaseClient
{
    private static IStorage? _storage;

    public static void Initialize(IStorage storage)
    {
        _storage = storage;
    }

    private static IStorage Storage =>
        _storage
        ?? throw new InvalidOperationException(
            "FanArtImageClient has not been initialized. Call FanArtImageClient.Initialize() at startup."
        );

    public FanArtImageClient() { }

    public FanArtImageClient(Guid id)
        : base(id) { }

    public Task<CoverArtCovers?> Cover(bool priority = false)
    {
        Dictionary<string, string?> queryParams = new()
        {
            //
        };

        return Get<CoverArtCovers>("release/" + Id, queryParams, priority);
    }

    public static async Task<MagickImage?> Download(
        Uri url,
        bool? download = true,
        Size? maxDecodeSize = null
    )
    {
        string filePath = Path.Combine(AppFiles.MusicImagesPath, Path.GetFileName(url.LocalPath));

        IStorage storage = Storage;
        if (await storage.ExistsAsync(filePath, CancellationToken.None))
            return new(filePath, MagickReadSettingsFactory.Create(maxDecodeSize));

        HttpClient httpClient = HttpClientProvider.CreateClient(HttpClientNames.FanArtImage);

        HttpResponseMessage queuedResponse;
        try
        {
            queuedResponse = await ProviderQueues
                .For(HttpClientNames.FanArtImage)
                .Enqueue(
                    async () =>
                    {
                        // Owned by Download after the queue returns; disposed here on retry.
                        HttpResponseMessage reply = await httpClient.GetAsync(url);
                        if (
                            reply.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                            || (int)reply.StatusCode >= 500
                        )
                        {
                            try
                            {
                                reply.EnsureProviderSuccess();
                            }
                            catch
                            {
                                reply.Dispose();
                                throw;
                            }
                        }
                        return reply;
                    },
                    url.ToString()
                );
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                || (int?)ex.StatusCode is >= 500 and <= 599
            )
        {
            return null;
        }
        using HttpResponseMessage response = queuedResponse;
        if (!response.IsSuccessStatusCode)
            return null;
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();

        if (download is not false && !await storage.ExistsAsync(filePath, CancellationToken.None))
            await storage.WriteAsync(filePath, bytes, CancellationToken.None);

        return new(bytes, MagickReadSettingsFactory.Create(maxDecodeSize));
    }
}
