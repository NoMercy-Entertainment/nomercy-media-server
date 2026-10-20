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

using NoMercy.Encoder.Pipeline.Stages;
using LegacyDrmConfig = NoMercy.Encoder.BuildingBlocks.Drm.DrmConfig;
using ProfileDrmConfig = NoMercy.Encoder.Profiles.DrmConfig;

namespace NoMercy.Tests.Encoder.Pipeline.Planners;

/// <summary>
/// DrmConfigConverter turns a profile's drm block into the config the
/// packagers consume. Whatever the profile validator lets through must come
/// out the other side intact: a key the validator accepted but the converter
/// dropped only surfaces as a packaging failure at encode time.
/// </summary>
public class DrmConfigConverterTests
{
    [Fact]
    public void Convert_Null_ReturnsNull()
    {
        DrmConfigConverter.Convert(null).Should().BeNull();
    }

    [Theory]
    [InlineData("key_uri")]
    [InlineData("key_url")]
    [InlineData("license_url")]
    [InlineData("keyfile")]
    [InlineData("key_file")]
    [InlineData("Key_Uri")]
    [InlineData("KEY_URL")]
    public void Convert_EveryKeyNameTheValidatorAccepts_ReachesKeyUri(string parameterName)
    {
        ProfileDrmConfig profileDrm = new("aes128", new() { [parameterName] = "u" });

        LegacyDrmConfig? result = DrmConfigConverter.Convert(profileDrm);

        result.Should().NotBeNull();
        result!.KeyUri.Should().Be("u");
    }
}
