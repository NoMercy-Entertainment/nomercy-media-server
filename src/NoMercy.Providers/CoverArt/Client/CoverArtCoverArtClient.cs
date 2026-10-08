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
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;
using NoMercy.Providers.CoverArt.Models;
using NoMercy.Providers.Helpers;
using NoMercy.Setup.Server;
using NoMercy.Storage;
using Configuration = AcoustID.Configuration;
using HttpClient = System.Net.Http.HttpClient;

namespace NoMercy.Providers.CoverArt.Client;

public class CoverArtCoverArtClient : CoverArtBaseClient
{
    private static IStorage? _storage;

    public static void Initialize(IStorage storage)
    {
        _storage = storage;
    }

    private static IStorage Storage =>
        _storage
        ?? throw new InvalidOperationException(
            "CoverArtCoverArtClient has not been initialized. Call CoverArtCoverArtClient.Initialize() at startup."
        );

    public CoverArtCoverArtClient(Guid id)
        : base(id)
    {
        Configuration.ClientKey = ApiKeyStore.Current.AcousticIdKey;
    }

    public Task<CoverArtCovers?> Cover(bool priority = false)
    {
        Dictionary<string, string?> queryParams = new()
        {
            //
        };

        try
        {
            return Get<CoverArtCovers>("release/" + Id, queryParams, priority);
        }
        catch (Exception)
        {
            return Task.FromResult<CoverArtCovers?>(null);
        }
    }

    public Task<CoverArtCovers?> GroupCover(bool priority = false)
    {
        Dictionary<string, string?> queryParams = new()
        {
            //
        };

        try
        {
            return Get<CoverArtCovers>("release-group/" + Id, queryParams, priority);
        }
        catch (Exception)
        {
            return Task.FromResult<CoverArtCovers?>(null);
        }
    }

    public static async Task<MagickImage?> Download(
        Uri? url,
        bool? download = true,
        Size? maxDecodeSize = null
    )
    {
        string filePath = Path.Combine(
            AppFiles.MusicImagesPath,
            Path.GetFileName((url?.LocalPath).OrEmpty())
        );

        IStorage storage = Storage;
        if (await storage.ExistsAsync(filePath, CancellationToken.None))
            return new(filePath, MagickReadSettingsFactory.Create(maxDecodeSize));

        HttpClient httpClient = HttpClientProvider.CreateClient(HttpClientNames.CoverArtImage);

        using HttpResponseMessage response = await httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode)
            return null;

        byte[] bytes = await response.Content.ReadAsByteArrayAsync();

        if (download is not false && !await storage.ExistsAsync(filePath, CancellationToken.None))
            await storage.WriteAsync(filePath, bytes, CancellationToken.None);

        return new(bytes, MagickReadSettingsFactory.Create(maxDecodeSize));
    }
}
