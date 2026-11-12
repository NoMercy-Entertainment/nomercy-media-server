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
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Api.Security;

// Read straight from the store on every access: a threshold changed in the
// dashboard has to take effect on the next request, not on the next restart.
public class AbuseGuardSettings(IConfigurationStore configurationStore) : IAbuseGuardSettings
{
    public const string EnabledKey = "security.abuse_guard.enabled";
    public const string MaxScoreKey = "security.abuse_guard.max_score";
    public const string WindowMinutesKey = "security.abuse_guard.window_minutes";
    public const string BanMinutesKey = "security.abuse_guard.ban_minutes";
    public const string MaxBanMinutesKey = "security.abuse_guard.max_ban_minutes";
    public const string AllowlistKey = "security.abuse_guard.allowlist";

    public bool Enabled => ReadBool(EnabledKey, true);

    public int MaxScore => ReadInt(MaxScoreKey, 10);

    public TimeSpan Window => TimeSpan.FromMinutes(ReadInt(WindowMinutesKey, 10));

    public TimeSpan BanDuration => TimeSpan.FromMinutes(ReadInt(BanMinutesKey, 60));

    public TimeSpan MaxBanDuration => TimeSpan.FromMinutes(ReadInt(MaxBanMinutesKey, 10080));

    public string AllowlistText => configurationStore.GetValue(AllowlistKey) ?? string.Empty;

    public IReadOnlyList<IpRange> Allowlist
    {
        get
        {
            string raw = AllowlistText;
            if (string.IsNullOrWhiteSpace(raw))
                return [];

            List<IpRange> ranges = [];
            foreach (string entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (IpRange.TryParse(entry, out IpRange range))
                    ranges.Add(range);

            return ranges;
        }
    }

    public Task SetAsync(string key, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return configurationStore.SetValueAsync(key, value);
    }

    private bool ReadBool(string key, bool fallback)
    {
        string? raw = configurationStore.GetValue(key);
        return bool.TryParse(raw, out bool parsed) ? parsed : fallback;
    }

    private int ReadInt(string key, int fallback)
    {
        string? raw = configurationStore.GetValue(key);
        return int.TryParse(raw, out int parsed) && parsed > 0 ? parsed : fallback;
    }
}
