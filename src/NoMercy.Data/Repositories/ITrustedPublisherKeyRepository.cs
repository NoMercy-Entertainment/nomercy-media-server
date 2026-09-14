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
using NoMercy.Database.Models.Media;

namespace NoMercy.Data.Repositories;

/// <summary>Ed25519 publisher keys whose signed encoder profiles the server trusts.</summary>
public interface ITrustedPublisherKeyRepository
{
    /// <summary>All trusted keys, oldest first.</summary>
    Task<List<TrustedPublisherKey>> GetAllAsync(CancellationToken ct = default);

    Task<bool> ExistsAsync(string fingerprint, CancellationToken ct = default);

    Task AddAsync(TrustedPublisherKey key, CancellationToken ct = default);

    /// <summary>Removes the key; false when no key has that fingerprint.</summary>
    Task<bool> DeleteAsync(string fingerprint, CancellationToken ct = default);
}
