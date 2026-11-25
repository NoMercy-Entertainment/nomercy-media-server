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

using NoMercy.Database;

namespace NoMercy.Queue.MediaServer.Jobs;

/// <summary>One image-path column: the entity, its property, and the query that reads it.</summary>
public sealed record ImageColumn(
    Type Entity,
    string Property,
    Func<MediaContext, IQueryable<string?>> Query
);
