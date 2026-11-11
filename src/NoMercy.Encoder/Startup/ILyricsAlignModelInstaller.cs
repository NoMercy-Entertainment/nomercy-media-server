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

namespace NoMercy.Encoder.Startup;

/// <summary>
/// Fetches the lyricsalign model the first time something needs it.
/// <para>
/// Every other model the bundled ffmpeg uses is downloaded at setup. This one
/// is 600 MB and only a plugin that aligns lyrics to audio reads it, so a
/// server nobody aligns lyrics on never pays for it. The first plugin graph
/// that names the model asks for it here, is refused, and runs once the file
/// has arrived.
/// </para>
/// </summary>
public interface ILyricsAlignModelInstaller
{
    /// <summary>
    /// Starts the download in the background and returns at once. Does
    /// nothing while a download is running, or shortly after one ended, so a
    /// plugin sweeping a library can ask once per track without starting a
    /// download per track.
    /// </summary>
    void RequestInstall();
}
