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

using System.Globalization;
using NoMercy.Encoder.Composition;

namespace NoMercy.Service.Configuration;

/// <summary>
/// Reads the distributed-encoding settings into <see cref="EncoderOptions"/> from
/// the "Encoder" configuration section: appsettings.json, or environment variables
/// such as <c>Encoder__CoordinatorUrl</c>. The key names are the property names
/// docs/distributed-encoding.md documents. Before this nothing read them, so a
/// coordinator never had a signing key and a worker never had a coordinator to
/// register with (#228).
///
/// A missing or blank value keeps the built-in default, so an install without
/// these settings behaves exactly as before. A value that is present but unsafe
/// is refused or corrected, and <c>warn</c> receives one line saying so.
/// </summary>
public static class EncoderDistributionSettings
{
    public const string SectionName = "Encoder";

    /// <summary>
    /// Shortest signing key accepted from configuration: the key is the only
    /// credential between coordinator and workers, and 32 characters is what
    /// <c>openssl rand -base64 32</c> comfortably exceeds.
    /// </summary>
    public const int MinimumSigningKeyLength = 32;

    /// <summary>
    /// The coordinator drops a worker after 60 s without a heartbeat
    /// (InMemoryRemoteWorkerRegistry), so a worker must beat at least twice in
    /// that time; faster than once a second only adds load.
    /// </summary>
    public static readonly TimeSpan MinimumHeartbeatInterval = TimeSpan.FromSeconds(1);

    /// <inheritdoc cref="MinimumHeartbeatInterval"/>
    public static readonly TimeSpan MaximumHeartbeatInterval = TimeSpan.FromSeconds(30);

    public static void Apply(
        IConfiguration configuration,
        EncoderOptions options,
        Action<string> warn
    )
    {
        IConfigurationSection section = configuration.GetSection(SectionName);

        string signingKeyName =
            $"{SectionName}:{nameof(EncoderOptions.DistributedEncodingSigningKey)}";
        if (Read(section, nameof(EncoderOptions.DistributedEncodingSigningKey)) is { } signingKey)
        {
            if (signingKey.Length >= MinimumSigningKeyLength)
                options.DistributedEncodingSigningKey = signingKey;
            else
                warn(
                    $"{signingKeyName} is shorter than {MinimumSigningKeyLength} characters, so "
                        + "distributed encoding stays off. Generate a key with "
                        + "'openssl rand -base64 32' and use the same one on every machine."
                );
        }

        if (Read(section, nameof(EncoderOptions.CoordinatorUrl)) is { } coordinatorUrl)
            options.CoordinatorUrl = coordinatorUrl;

        if (Read(section, nameof(EncoderOptions.WorkerSelfBaseUrl)) is { } workerSelfBaseUrl)
            options.WorkerSelfBaseUrl = workerSelfBaseUrl;

        if (Read(section, nameof(EncoderOptions.WorkerId)) is { } workerId)
            options.WorkerId = workerId;

        if (Read(section, nameof(EncoderOptions.WorkerHeartbeatInterval)) is { } interval)
            ApplyHeartbeatInterval(interval, options, warn);
    }

    private static void ApplyHeartbeatInterval(
        string value,
        EncoderOptions options,
        Action<string> warn
    )
    {
        string name = $"{SectionName}:{nameof(EncoderOptions.WorkerHeartbeatInterval)}";

        if (
            !TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan interval)
            || interval <= TimeSpan.Zero
        )
        {
            warn(
                $"{name} '{value}' is not a positive time span such as 00:00:20; "
                    + $"keeping {options.WorkerHeartbeatInterval}."
            );
            return;
        }

        TimeSpan clamped =
            interval < MinimumHeartbeatInterval ? MinimumHeartbeatInterval
            : interval > MaximumHeartbeatInterval ? MaximumHeartbeatInterval
            : interval;
        if (clamped != interval)
            warn(
                $"{name} {interval} is outside {MinimumHeartbeatInterval} to "
                    + $"{MaximumHeartbeatInterval}; using {clamped}."
            );

        options.WorkerHeartbeatInterval = clamped;
    }

    // The value is used as given: the signing key must match the other side byte for byte.
    private static string? Read(IConfigurationSection section, string key) =>
        string.IsNullOrWhiteSpace(section[key]) ? null : section[key];
}
