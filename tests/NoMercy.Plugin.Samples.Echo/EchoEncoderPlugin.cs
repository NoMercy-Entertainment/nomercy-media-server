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

public class EchoEncoderPlugin : IEncoderPlugin
{
    public string Name => "Echo";
    public string Description => "Sample plugin — returns a fixed encoding profile for any input.";
    public Ulid Id { get; } = Ulid.Parse("01ECH000000000000000000000");
    public Version Version { get; } = new(0, 1, 0);

    public void Initialize(IPluginContext context)
    {
        context.Logger.LogInformation("Echo plugin initialized");
    }

    public EncodingProfile GetProfile(MediaInfo info) =>
        new()
        {
            Name = $"Echo-{Path.GetFileName(info.FilePath)}",
            VideoCodec = "h264",
            AudioCodec = "aac",
        };

    // Test-only observability hook: PluginLoader loads this sample into an
    // isolated AssemblyLoadContext, so a static field set from the host test
    // process is NOT visible to this instance (different ALC, different
    // static storage). An env var IS process-wide regardless of ALC, so it's
    // the one side channel that lets a disposal test prove Dispose() ran on
    // an instance the host never got a reference back to. No-ops when unset —
    // every other test that loads this sample is unaffected.
    public void Dispose()
    {
        string? markerPath = Environment.GetEnvironmentVariable(
            "NOMERCY_TEST_ECHO_DISPOSAL_MARKER"
        );
        if (markerPath is not null)
            File.WriteAllText(markerPath, "disposed");
    }
}
