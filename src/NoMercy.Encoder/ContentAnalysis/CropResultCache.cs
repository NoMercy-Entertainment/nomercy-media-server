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

using System.Collections.Concurrent;

namespace NoMercy.Encoder.ContentAnalysis;

/// <summary>
/// Crop results keyed by source file identity (path, size, modified time), shared
/// by every bundle of an encode so the 3-minute cropdetect scan runs once.
/// </summary>
public sealed class CropResultCache
{
    private const int MaxEntries = 256;

    private readonly ConcurrentDictionary<CropCacheKey, CropResult> _entries = new();

    public bool TryGet(CropCacheKey key, out CropResult? result) =>
        _entries.TryGetValue(key, out result);

    public void Set(CropCacheKey key, CropResult result)
    {
        if (_entries.Count >= MaxEntries)
            _entries.Clear();

        _entries[key] = result;
    }
}

public readonly record struct CropCacheKey(
    string Path,
    long SizeBytes,
    long ModifiedUtcTicks,
    bool? SourceIsHdr
);
