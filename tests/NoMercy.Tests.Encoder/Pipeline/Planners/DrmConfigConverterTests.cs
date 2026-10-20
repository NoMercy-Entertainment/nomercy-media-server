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
using LegacyDrmMethod = NoMercy.Encoder.BuildingBlocks.Drm.DrmMethod;
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

    [Theory]
    [InlineData("aes128", LegacyDrmMethod.Aes128)]
    [InlineData("AES-128", LegacyDrmMethod.Aes128)]
    [InlineData("cenc", LegacyDrmMethod.Cenc)]
    [InlineData("none", LegacyDrmMethod.None)]
    public void Convert_SupportedScheme_MapsToItsMethod(string scheme, LegacyDrmMethod expected)
    {
        ProfileDrmConfig profileDrm = new(scheme, new() { ["key_uri"] = "u" });

        DrmConfigConverter.Convert(profileDrm)!.Method.Should().Be(expected);
    }

    /// <summary>
    /// A scheme the pipeline cannot package used to convert to Method None,
    /// which BuildStage reads as "no DRM": the key was ignored and the encode
    /// shipped in plaintext while the profile said it was encrypted.
    /// </summary>
    [Theory]
    [InlineData("cbcs")]
    [InlineData("aes-128-cbc")]
    [InlineData("aes_128")]
    public void Convert_UnsupportedScheme_FailsInsteadOfPackagingUnencrypted(string scheme)
    {
        ProfileDrmConfig profileDrm = new(scheme, new() { ["key_uri"] = "u" });

        Action act = () => DrmConfigConverter.Convert(profileDrm);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage($"*'{scheme}'*")
            .WithMessage("*aes128*")
            .WithMessage("*cenc*");
    }
}
