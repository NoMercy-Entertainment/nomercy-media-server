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

namespace NoMercy.PluginSdk;

/// <summary>
/// The update was unpacked and verified, and is waiting for a restart because
/// the copy it replaces is still loaded.
/// <para>
/// Not a failure. Nothing was lost and nothing has to be done again: the new
/// version is on disk and the next start applies it before a single assembly is
/// loaded. This exists so the caller can say that plainly instead of reporting
/// the <see cref="IOException" /> that used to reach the owner as a 500 — the
/// file being locked is the expected outcome of updating a running plugin on
/// Windows, not something that went wrong.
/// </para>
/// </summary>
public class PluginUpdatePendingRestartException(string folderName)
    : Exception(
        $"The update to {folderName} is ready and applies when the server restarts: "
            + "its files are still in use and cannot be changed until then."
    )
{
    /// <summary>The plugin folder the staged update belongs to.</summary>
    public string FolderName { get; } = folderName;

    /// <summary>
    /// Why a restart is needed, in the vocabulary the dashboard already draws
    /// for enable and uninstall, so this reads as the same kind of answer
    /// rather than a second one invented here.
    /// </summary>
    public PluginRestartRequirement Restart { get; } =
        new(PluginRestartReason.AssemblyStillLoaded);
}
