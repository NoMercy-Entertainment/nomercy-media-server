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

using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Libraries;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Information;
using NoMercy.OpticalMedia.Drives;
using NoMercy.OpticalMedia.Sources;
using NoMercy.Storage;

namespace NoMercy.OpticalMedia.Rip;

public class DiscRipPreparationService(
    DiscSourceFactory discSourceFactory,
    ILibraryRepository libraryRepository,
    IStorageDriver storageDriver
) : IDiscRipPreparationService
{
    public async Task<DiscRipPreparationResult> PrepareAsync(
        DiscDrive drive,
        RipRequest request,
        CancellationToken ct
    )
    {
        // Fail fast if the disc is DRM-locked the host can't read.
        IDiscSource? source = discSourceFactory.CreateFor(drive.DiscType);
        if (source is not null)
        {
            DiscInfo precheck = await source.ProbeAsync(drive, ct);
            if (precheck.Protection is not null)
                return new()
                {
                    Success = false,
                    ErrorMessage =
                        $"Cannot rip — disc is {precheck.Protection.Kind}-protected: {precheck.Protection.Message}",
                };
        }

        Folder? targetFolder = null;
        Library? targetLibrary = null;
        if (request.Mode == RipMode.RipAndEncode)
        {
            targetFolder = await libraryRepository.GetLibraryFolder(request.FolderId);
            if (targetFolder is null)
                return new()
                {
                    Success = false,
                    ErrorMessage =
                        $"FolderId {request.FolderId} does not match any library folder. "
                        + "RipAndEncode needs a real folder so the rip output lands somewhere "
                        + "the encoder can read it via the folder's driver.",
                };

            targetLibrary = await libraryRepository.GetLibraryByIdWithFolders(request.LibraryId);
            if (targetLibrary is null)
                return new()
                {
                    Success = false,
                    ErrorMessage = $"LibraryId {request.LibraryId} does not match any library.",
                };
        }

        string sanitisedDrive = drive
            .Path.TrimEnd(Path.DirectorySeparatorChar)
            .Replace(":", "")
            .Replace(Path.DirectorySeparatorChar, '_');
        string outputDir = Path.Combine(AppFiles.TranscodePath, "ripper", sanitisedDrive);
        storageDriver.CreateDirectory(outputDir);

        // For audio CDs, default to all probed tracks when the caller sent
        // no SelectedTitleIndices (CD tracks don't map to video-title semantics).
        RipRequest enriched = request with
        {
            DiscType = drive.DiscType,
        };

        if (drive.DiscType == OpticalDiscType.Cd && enriched.SelectedTitleIndices.Length == 0)
        {
            IDiscSource? cdSource = discSourceFactory.CreateFor(OpticalDiscType.Cd);
            if (cdSource is not null)
            {
                DiscInfo cdInfo = await cdSource.ProbeAsync(drive, ct);
                if (cdInfo.AudioTracks is { Length: > 0 })
                {
                    enriched = enriched with
                    {
                        SelectedTitleIndices = cdInfo.AudioTracks.Select(t => t.Index).ToArray(),
                    };
                }
            }
        }

        return new()
        {
            Success = true,
            EnrichedRequest = enriched,
            TargetFolderId = targetFolder?.Id,
            TargetLibraryId = targetLibrary?.Id,
            TargetLibraryType = targetLibrary?.Type,
            OutputDir = outputDir,
        };
    }
}
