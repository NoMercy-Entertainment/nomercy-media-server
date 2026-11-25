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

using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Data.ServerBackups;

// A missing or unreadable value falls back to the default, so an older install keeps working.
public class BackupSettings(IConfigurationStore configurationStore)
{
    public const string RetainCountKey = "backup.retain_count";

    public const string PruneHandMadeCopiesKey = "backup.prune_hand_made_copies";

    /// <summary>Off unless "true": a copy made by hand cannot be made again.</summary>
    public bool PruneHandMadeCopies =>
        bool.TryParse(configurationStore.GetValue(PruneHandMadeCopiesKey), out bool parsed)
        && parsed;

    public int RetainCount =>
        int.TryParse(configurationStore.GetValue(RetainCountKey), out int parsed) && parsed >= 1
            ? parsed
            : BackupOptions.DefaultRetainCount;
}
