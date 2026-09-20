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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// Offering a finished file to one of the owner's libraries.
/// <para>
/// A plugin that downloads or records had nowhere to hand the result. What it
/// did instead was write into a library folder and hope the server's own
/// scanner noticed, which meant a half-written file could be picked up mid-copy
/// and filed as a broken episode the owner then had to delete twice.
/// </para>
/// <para>
/// Registering says the file is finished. The server does the filing, so the
/// plugin never has to know how this library names its folders.
/// </para>
/// </summary>
public interface IPluginLibraryImport
{
    Task<PluginImportResult> RegisterAsync(
        PluginImportRequest request,
        CancellationToken ct = default
    );

    /// <summary>
    /// The same offer for a file still being written, which is what a recording
    /// in progress is. The library shows it as recording and the import
    /// completes when the stream ends, so a viewer can start watching a
    /// program that has not finished airing.
    /// </summary>
    Task<PluginImportResult> StreamAsync(
        PluginImportRequest request,
        CancellationToken ct = default
    );
}
