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

namespace NoMercy.Api.Services;

/// <summary>The maintenance settings in effect, with their defaults filled in.</summary>
public sealed record StorageSettings(
    int DatabaseHour,
    int FailedJobRetentionDays,
    int BackupRetainCount,
    int ImageCacheMaxMegabytes,
    int ImageCacheOrphanGraceDays,
    int ImageCacheHour,
    bool BackupPruneHandMadeCopies
);
