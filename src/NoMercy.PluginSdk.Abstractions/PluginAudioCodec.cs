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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// What one output of <see cref="IPluginAudioTools.RenderAsync" /> is encoded
/// as. The codec also decides the container, so a file is what its codec says
/// it is whatever name the plugin gave it. Added in ABI 12.7.
/// </summary>
public enum PluginAudioCodec
{
    /// <summary>Opus in an Ogg container: for something a listener hears.</summary>
    Opus = 0,

    /// <summary>FLAC: a lossless intermediate at about half the size of WAV.</summary>
    Flac = 1,

    /// <summary>16-bit PCM in a WAV container: a lossless intermediate any tool opens.</summary>
    Wav = 2,
}
