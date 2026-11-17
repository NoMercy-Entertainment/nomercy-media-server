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

namespace NoMercy.PluginSdk.Library;

/// <summary>
/// Whether one plugin may write to one library, without describing every
/// library to find out.
/// <para>
/// <see cref="IPluginLibraryWriter.GetWritableLibrariesAsync"/> answers the
/// whole library list with how each one is encoded, which costs three table
/// reads and a preset resolve per call. An import asks the yes-or-no question
/// for every file it files, so a server writer offers this cheaper path beside
/// the contract. Server-side only: not plugin ABI.
/// </para>
/// </summary>
public interface IPluginLibraryWriteCheck
{
    /// <summary>
    /// True when the plugin holds a write grant for <paramref name="libraryId"/>
    /// and that library exists: exactly the libraries
    /// <see cref="IPluginLibraryWriter.GetWritableLibrariesAsync"/> lists.
    /// </summary>
    Task<bool> MayWriteToLibraryAsync(string libraryId, CancellationToken ct = default);
}
