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

using NoMercy.Storage;

namespace NoMercy.Setup.Server;

public enum StagingState
{
    /// <summary>A container updates by pulling a new image, never by swapping the binary.</summary>
    ContainerImage,

    /// <summary>A staged binary newer than the running server is waiting for a restart.</summary>
    AlreadyStaged,

    /// <summary>The server binary on disk is already newer; a restart applies it.</summary>
    BinaryOnDiskIsNewer,

    NeedsDownload,
}

/// <param name="Version">The staged or on-disk version the state refers to, when there is one.</param>
/// <param name="DiscardedStaleVersion">Set when a staged binary that was not newer got deleted.</param>
public sealed record StagingCheck(
    StagingState State,
    string? Version = null,
    string? DiscardedStaleVersion = null
);

/// <summary>
/// Decides what an on-demand server update has to do before anything is downloaded.
/// </summary>
public static class ServerUpdateStaging
{
    /// <param name="fileVersion">Reads a binary's file version; null when it has none.</param>
    public static StagingCheck Check(
        IStorageDriver storageDriver,
        bool isContainer,
        string runningVersion,
        string stagedPath,
        string serverPath,
        Func<string, string?> fileVersion
    )
    {
        // Deployment type is decided before anything else. A container keeps its data
        // volume across image updates, so a staging file written by some earlier attempt
        // outlives every upgrade — and when the staged check ran first, that one stale
        // file answered "already staged" forever and no update ever happened again.
        if (isContainer)
        {
            if (storageDriver.FileExists(stagedPath))
                storageDriver.DeleteFile(stagedPath);
            return new(StagingState.ContainerImage);
        }

        string? discarded = null;

        // Existence alone is not proof the staged file is the update anyone wants: a file
        // from a previous, older attempt claims the slot just as convincingly.
        if (storageDriver.FileExists(stagedPath))
        {
            string? stagedVersion = fileVersion(stagedPath);
            if (IsNewer(stagedVersion, runningVersion))
                return new(StagingState.AlreadyStaged, stagedVersion);

            storageDriver.DeleteFile(stagedPath);
            discarded = stagedVersion ?? "unknown";
        }

        string? onDiskVersion = fileVersion(serverPath);
        if (IsNewer(onDiskVersion, runningVersion))
            return new(StagingState.BinaryOnDiskIsNewer, onDiskVersion, discarded);

        return new(StagingState.NeedsDownload, DiscardedStaleVersion: discarded);
    }

    public static bool IsNewer(string? candidate, string runningVersion) =>
        candidate is not null
        && System.Version.TryParse(candidate, out Version? candidateVersion)
        && System.Version.TryParse(runningVersion, out Version? running)
        && candidateVersion > running;
}
