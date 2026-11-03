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

namespace NoMercy.NmSystem.Wallpaper;

public interface IWallpaperService
{
    bool IsSupported { get; }

    /// <summary>Applies the wallpaper. False when the desktop did not accept it.</summary>
    bool Set(string imagePath, WallpaperStyle style, string hexColor);

    /// <summary>
    /// Applies the wallpaper without leaving it in the desktop's history.
    /// False when the desktop did not accept it.
    /// </summary>
    bool SetSilent(string imagePath, WallpaperStyle style, string hexColor);

    void Restore();
}
