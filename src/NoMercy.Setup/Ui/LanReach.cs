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

using NoMercy.NmSystem.Information;

namespace NoMercy.Setup.Ui;

// macOS Local Network privacy drops LAN traffic to a server the owner has not allowed,
// while localhost keeps working (#660). A request to our own LAN address shows it.
public static class LanReach
{
    public const string MacOsStep =
        "Other devices on your network cannot reach this server: macOS blocks it. "
        + "Open System Settings > Privacy & Security > Local Network and turn on NoMercy MediaServer.";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    /// <summary>True after <see cref="CheckMacOsAsync"/> found the LAN address blocked.</summary>
    public static bool Blocked { get; private set; }

    /// <summary>The step to show the owner, or null when nothing is blocked.</summary>
    public static string? Step => Blocked ? MacOsStep : null;

    public static async Task<bool> IsBlockedAsync(
        SetupAddress address,
        Func<string, Task<bool>> answers
    )
    {
        if (address.NetworkUrl is null)
            return false;
        if (!await answers(address.LocalhostUrl))
            return false;
        return !await answers(address.NetworkUrl);
    }

    public static async Task<bool> CheckMacOsAsync(SetupAddress address)
    {
        if (!Software.IsMac)
            return false;

        using HttpClient client = new() { Timeout = ProbeTimeout };
        Blocked = await IsBlockedAsync(address, url => AnswersAsync(client, url));
        return Blocked;
    }

    // Any HTTP response counts as reachable; only a timeout or a refused connection does not.
    private static async Task<bool> AnswersAsync(HttpClient client, string url)
    {
        try
        {
            using HttpResponseMessage _ = await client.GetAsync(url);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
