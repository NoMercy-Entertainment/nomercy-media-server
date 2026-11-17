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
/// One encoding preset a library's files are encoded to, as
/// <see cref="PluginLibrary.EncodingPresets"/> lists it. Added in ABI 12.11.
/// </summary>
/// <param name="Id">The preset's id.</param>
/// <param name="Name">As the dashboard shows it.</param>
/// <param name="IsDefault">Linked to a folder of this library as its default.</param>
/// <param name="MaxHeight">
/// The tallest video this preset can produce: the highest tier of an Auto
/// ladder, the highest rung of a Manual one, or <c>Video.Height</c> without a
/// ladder. Null when the preset keeps the source's size, or makes no video.
/// </param>
public record PluginEncodingPreset(string Id, string Name, bool IsDefault, int? MaxHeight);
