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

using NoMercy.Networking.Http;

namespace NoMercy.Api.Security;

public interface IAbuseGuardSettings
{
    bool Enabled { get; }

    int MaxScore { get; }

    TimeSpan Window { get; }

    TimeSpan BanDuration { get; }

    TimeSpan MaxBanDuration { get; }

    IReadOnlyList<IpRange> Allowlist { get; }

    /// <summary>The allowlist exactly as it was typed, for round-tripping into the dashboard.</summary>
    string AllowlistText { get; }

    Task SetAsync(string key, string value, CancellationToken ct);
}
