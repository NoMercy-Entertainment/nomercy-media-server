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

using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Providers.CoverArt.Client;
using NoMercy.Providers.CoverArt.Models;
using Serilog.Events;
using Size = System.Drawing.Size;

namespace NoMercy.MediaProcessing.Images;

public class CoverArtImageManagerManager : ICoverArtImageManagerManager
{
    private static readonly Size PaletteDecodeSize = new(
        ColorQuantizer.MaxDimension,
        ColorQuantizer.MaxDimension
    );

    public static async Task<string> ColorPalette(string type, Uri url, bool? download = true)
    {
        return await BaseImageManager.ColorPalette(
            CoverArtCoverArtClient.Download,
            type,
            url,
            download,
            PaletteDecodeSize
        );
    }

    public async Task<string> MultiColorPalette(
        IEnumerable<BaseImageManager.MultiUriType> items,
        bool? download = true
    )
    {
        return await BaseImageManager.MultiColorPalette(
            CoverArtCoverArtClient.Download,
            items,
            download,
            PaletteDecodeSize
        );
    }

    public class CoverPalette
    {
        public string? Palette { get; set; }
        public Uri? Url { get; set; }
    }

    public static async Task<Uri?> GetCoverUrl(Guid id, bool priority = false)
    {
        try
        {
            CoverArtCoverArtClient coverArtCoverArtClient = new(id);
            CoverArtCovers? covers = await coverArtCoverArtClient.Cover(priority);
            if (covers is null)
                return null;

            CoverArtImage? coverItem = covers.Images.FirstOrDefault(image =>
                image.Types.Contains("Front")
            );

            return coverItem?.CoverArtThumbnails.Large;
        }
        catch (Exception e)
        {
            if (e.Message.Contains("404"))
                return null;
            Logger.FanArt(e.Message, LogEventLevel.Verbose);
            return null;
        }
    }

    public static async Task<CoverPalette?> Add(Guid id, bool priority = false)
    {
        try
        {
            CoverArtCoverArtClient coverArtCoverArtClient = new(id);
            CoverArtCovers? covers = await coverArtCoverArtClient.GroupCover(priority);
            if (covers is null)
                return null;

            List<CoverArtImage> coverList = covers
                .Images.Where(image => image.Types.Contains("Front"))
                .ToList();

            foreach (CoverArtImage coverItem in coverList)
            {
                if (!coverItem.CoverArtThumbnails.Large.HasSuccessStatus("image/*"))
                    continue;

                return new()
                {
                    Palette = await ColorPalette("cover", coverItem.CoverArtThumbnails.Large),
                    Url = coverItem.CoverArtThumbnails.Large,
                };
            }

            return null;
        }
        catch (Exception e)
        {
            if (e.Message.Contains("404"))
                return null;
            Logger.FanArt(e.Message, LogEventLevel.Verbose);
            return null;
        }
    }
}
