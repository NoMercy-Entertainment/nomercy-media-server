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

using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Data.Plugins;

/// <summary>
/// The server side of <see cref="IPluginEncoder" />.
///
/// <para>
/// This is the work a plugin was doing by reflection: resolve the library, find
/// the folder it writes into, build the encode job and dispatch it. Every
/// refusal below is a case that really happened and was, until now, invisible -
/// the plugin dispatched, nothing encoded, and no line anywhere said why.
/// </para>
/// </summary>
public class PluginEncoder(
    IDbContextFactory<MediaContext> contextFactory,
    IJobDispatcher jobDispatcher
) : IPluginEncoder
{
    public async Task<PluginEncodeResult> EncodeAsync(
        string file,
        string libraryId,
        string? mediaId = null,
        string? presetId = null,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(file))
            return PluginEncodeResult.Refused("no file was named");

        if (!Ulid.TryParse(libraryId, out Ulid library))
            return PluginEncodeResult.Refused(
                $"'{libraryId}' is not a library id this server issues"
            );

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        Library? target = await context
            .Libraries.AsNoTracking()
            .Include(item => item.FolderLibraries)
            .FirstOrDefaultAsync(item => item.Id == library, ct);

        if (target is null)
            return PluginEncodeResult.Refused("the server knows no library with that id");

        // A library with nowhere to put anything is the first refusal an owner
        // ever hits, and it used to present as an encode that never started.
        FolderLibrary? folder = target.FolderLibraries.FirstOrDefault();

        if (folder is null)
            return PluginEncodeResult.Refused(
                $"the library '{target.Title}' has no folder to put anything in"
            );

        Ulid? preset = null;

        if (!string.IsNullOrWhiteSpace(presetId))
        {
            if (!Ulid.TryParse(presetId, out Ulid parsed))
                return PluginEncodeResult.Refused(
                    $"'{presetId}' is not a preset id this server issues"
                );

            preset = parsed;
        }

        VideoEncodeJob job = new()
        {
            // The row the encode registers its result against. Empty is what a
            // plugin had to send before PluginLibraryEpisode carried an id, and
            // it resolves to nothing: the queue counter moves, the library stays
            // empty, and from outside it is indistinguishable from an encode
            // still running.
            Id = mediaId ?? string.Empty,
            LibraryId = target.Id,
            FolderId = folder.FolderId,
            InputFile = file,
            PresetId = preset,
        };

        string? queued = jobDispatcher.DispatchTracked(job);

        // Null means an identical payload is already in the queue. Asking twice
        // for the same file is how a retry behaves, so it is not an error - but
        // a plugin holding that file needs to know it has no job to follow.
        return queued is null
            ? PluginEncodeResult.Refused("this exact encode is already queued")
            : PluginEncodeResult.Queued(queued);
    }
}
