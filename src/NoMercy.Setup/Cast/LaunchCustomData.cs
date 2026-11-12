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

using Newtonsoft.Json;

namespace NoMercy.Setup.Cast;

/// <summary>
/// LAUNCH customData bundle handed to the cast receiver on every cast session.
/// Mirrors the TypeScript shape in §8.2 of the cast-receiver-leanback spec.
///
/// The bundle is volatile — receiver holds these fields in memory only and
/// drops them when the cast process unloads. Refresh keeps the receiver alive
/// past sender disconnect.
///
/// JSON property names use snake_case to match the spec contract — the
/// receiver-side TypeScript types are authoritative.
/// </summary>
public class LaunchCustomData
{
    /// <summary>JWT issued by Keycloak's nomercy-cast-receiver client. ~12h TTL.</summary>
    [JsonProperty("access_token")]
    public string AccessToken { get; set; } = null!;

    /// <summary>Never set by the server any more; kept so an old payload still parses.</summary>
    [JsonProperty("refresh_token", NullValueHandling = NullValueHandling.Ignore)]
    public string? RefreshToken { get; set; }

    /// <summary>NoMercy user ID (Keycloak sub).</summary>
    [JsonProperty("user_id")]
    public string UserId { get; set; } = null!;

    /// <summary>NoMercy server ID. Receiver pins API calls to this server.</summary>
    [JsonProperty("server_id")]
    public string ServerId { get; set; } = null!;

    /// <summary>Public server URL — e.g. "https://abc123.nomercy.app".</summary>
    [JsonProperty("server_url")]
    public string ServerUrl { get; set; } = null!;

    /// <summary>Cast device ID matching Devices table row.</summary>
    [JsonProperty("device_id")]
    public string DeviceId { get; set; } = null!;

    /// <summary>What the receiver should do on boot.</summary>
    [JsonProperty("intent")]
    public CastIntent Intent { get; set; } = CastIntent.Idle();

    /// <summary>Server-generated UUID, unique per LAUNCH. Used for telemetry / correlation.</summary>
    [JsonProperty("cast_session_id")]
    public string CastSessionId { get; set; } = null!;

    /// <summary>UTC milliseconds at LAUNCH mint.</summary>
    [JsonProperty("launch_timestamp")]
    public long LaunchTimestamp { get; set; }

    /// <summary>Locale carried from sender — "en-US", "nl-NL", etc.</summary>
    [JsonProperty("client_locale")]
    public string ClientLocale { get; set; } = "en-US";

    /// <summary>Optional dev-only feature flags. Never set in prod.</summary>
    [JsonProperty("features", NullValueHandling = NullValueHandling.Ignore)]
    public CastFeatures? Features { get; set; }
}
