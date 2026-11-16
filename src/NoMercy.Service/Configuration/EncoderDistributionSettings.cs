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
/// these settings behaves exactly as before.
/// </summary>
public static class EncoderDistributionSettings
{
    public const string SectionName = "Encoder";

    public static void Apply(IConfiguration configuration, EncoderOptions options)
    {
        IConfigurationSection section = configuration.GetSection(SectionName);

        if (Read(section, nameof(EncoderOptions.DistributedEncodingSigningKey)) is { } signingKey)
            options.DistributedEncodingSigningKey = signingKey;

        if (Read(section, nameof(EncoderOptions.CoordinatorUrl)) is { } coordinatorUrl)
            options.CoordinatorUrl = coordinatorUrl;

        if (Read(section, nameof(EncoderOptions.WorkerSelfBaseUrl)) is { } workerSelfBaseUrl)
            options.WorkerSelfBaseUrl = workerSelfBaseUrl;

        if (Read(section, nameof(EncoderOptions.WorkerId)) is { } workerId)
            options.WorkerId = workerId;

        if (
            TimeSpan.TryParse(
                Read(section, nameof(EncoderOptions.WorkerHeartbeatInterval)),
                CultureInfo.InvariantCulture,
                out TimeSpan heartbeatInterval
            )
            && heartbeatInterval > TimeSpan.Zero
        )
            options.WorkerHeartbeatInterval = heartbeatInterval;
    }

    // The value is used as given: the signing key must match the other side byte for byte.
    private static string? Read(IConfigurationSection section, string key) =>
        string.IsNullOrWhiteSpace(section[key]) ? null : section[key];
}
