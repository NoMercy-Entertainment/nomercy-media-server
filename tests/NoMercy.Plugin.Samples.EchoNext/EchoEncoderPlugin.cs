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
using Microsoft.Extensions.Logging;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.Samples.Echo;

/// <summary>
/// The Echo sample's next release: same id, same assembly name, different
/// code. The only thing a test needs from it is a version the old build does
/// not report, so a hot update can be checked against what actually runs.
/// </summary>
public class EchoEncoderPlugin : IEncoderPlugin
{
    public string Name => "Echo";
    public string Description => "Sample plugin, second release.";
    public Ulid Id { get; } = Ulid.Parse("01ECH000000000000000000000");
    public Version Version { get; } = new(2, 0, 0);

    public void Initialize(IPluginContext context)
    {
        context.Logger.LogInformation("Echo plugin 2.0.0 initialized");
    }

    public EncodingProfile GetProfile(MediaInfo info) =>
        new()
        {
            Name = $"Echo2-{Path.GetFileName(info.FilePath)}",
            VideoCodec = "h264",
            AudioCodec = "aac",
        };

    public void Dispose() { }
}
