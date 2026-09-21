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
using NoMercy.Plugins.Storage;

namespace NoMercy.Data.Plugins;

/// <summary>
/// How much room is left on one of the server's folders.
/// <para>
/// Only a local folder can be answered honestly. An S3 bucket has no free
/// space in the sense a plugin means, and an NFS export's answer belongs to
/// whoever exported it; both read as not measurable rather than as a number
/// the plugin would then trust.
/// </para>
/// </summary>
public class PluginFolderFreeSpaceProbe(IDbContextFactory<MediaContext> contextFactory)
    : IPluginFreeSpaceProbe
{
    public const long NotMeasurable = -1;

    public async Task<long> FreeBytesAsync(string folderId, CancellationToken ct = default)
    {
        if (!Ulid.TryParse(folderId, out Ulid parsed))
            return NotMeasurable;

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        Folder? folder = await context
            .Folders.AsNoTracking()
            .Include(item => item.Driver)
            .FirstOrDefaultAsync(item => item.Id == parsed, ct);

        if (folder?.Driver is null)
            return NotMeasurable;

        if (!string.Equals(folder.Driver.Type, "local", StringComparison.OrdinalIgnoreCase))
            return NotMeasurable;

        try
        {
            return new DriveInfo(Path.GetPathRoot(folder.Path) ?? folder.Path).AvailableFreeSpace;
        }
        catch (Exception exception)
            when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A drive that is gone, or one this process may not ask about.
            // Either way the honest answer is that it cannot be measured.
            return NotMeasurable;
        }
    }
}
