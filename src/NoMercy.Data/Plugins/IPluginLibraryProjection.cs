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

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Data.Plugins;

/// <summary>
/// Every library, as a plugin sees it.
/// <para>
/// One answer for both <see cref="IPluginLibraryQuery.GetLibrariesAsync"/> and
/// <see cref="IPluginLibraryWriter.GetWritableLibrariesAsync"/>, so the library
/// a plugin reads and the library it may write to can never describe the same
/// row two different ways.
/// </para>
/// </summary>
public interface IPluginLibraryProjection
{
    /// <summary>Every library, with the encoding presets its folders link.</summary>
    Task<List<PluginLibrary>> GetLibrariesAsync(CancellationToken ct = default);
}
