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

using System.Security.Cryptography;
using FluentAssertions;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Verification;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A published checksum describes one thing: the .zip a plugin is released as.
/// The archive path hashed the zip and the bare-assembly path hashed the dll,
/// so the same catalogue entry could pass on one route and fail on the other,
/// and neither answer told a publisher which file to hash.
/// </summary>
public class PluginChecksumSubjectTests : IDisposable
{
    private readonly string _tempDir;

    public PluginChecksumSubjectTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nomercy-checksum-" + Ulid.NewUlid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string Write(string fileName, byte[] bytes)
    {
        string path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static PluginManifest Manifest() =>
        new()
        {
            Id = new PluginId(Ulid.NewUlid()),
            Name = "Internet Radio",
            Description = "d",
            Version = "1.2.0",
            Assembly = "NoMercy.Plugin.InternetRadio.dll",
            TargetAbi = PluginAbi.Current.ToString(),
        };

    [Fact]
    public void The_checksum_is_taken_over_the_package_not_the_assembly_inside_it()
    {
        byte[] packageBytes = [10, 20, 30, 40];
        string package = Write("InternetRadio-1.2.0.zip", packageBytes);
        string assembly = Write("NoMercy.Plugin.InternetRadio.dll", [1, 1, 1]);

        PluginVerificationResult result = new PluginVerifier().Verify(
            Manifest(),
            assembly,
            Sha256Of(packageBytes),
            package
        );

        result.Verified.Should().BeTrue();
        result.Trusted.Should().BeTrue();
    }

    [Fact]
    public void A_package_that_does_not_hash_to_what_was_published_is_refused()
    {
        string package = Write("InternetRadio-1.2.0.zip", [1, 2, 3]);
        string assembly = Write("NoMercy.Plugin.InternetRadio.dll", [1, 2, 3]);

        PluginVerificationResult result = new PluginVerifier().Verify(
            Manifest(),
            assembly,
            "deadbeef",
            package
        );

        result.Verified.Should().BeFalse();
        result.Failures.Should().ContainSingle();
        result.Failures[0].Should().Contain(PluginRefusalCode.ChecksumMismatch);
        result.Failures[0].Should().Contain("InternetRadio-1.2.0.zip");
    }

    /// <summary>
    /// The refusal names the file that was hashed and the file that should have
    /// been, so a publisher reading it knows which one to publish.
    /// </summary>
    [Fact]
    public void A_checksum_published_against_a_bare_assembly_is_refused_with_the_fix()
    {
        byte[] bytes = [7, 7, 7];
        string assembly = Write("NoMercy.Plugin.InternetRadio.dll", bytes);

        PluginVerificationResult result = new PluginVerifier().Verify(
            Manifest(),
            assembly,
            Sha256Of(bytes),
            assembly
        );

        result.Verified.Should().BeFalse();
        result.Failures[0].Should().Contain(PluginRefusalCode.ChecksumSubjectNotAPackage);
        result.Failures[0].Should().Contain(".zip");
    }

    /// <summary>
    /// A checksum with nothing to hash must refuse rather than pass: a caller
    /// that forgot to say which file arrived would otherwise install anything.
    /// </summary>
    [Fact]
    public void A_checksum_with_no_package_to_hash_is_refused()
    {
        string assembly = Write("NoMercy.Plugin.InternetRadio.dll", [4, 4]);

        PluginVerificationResult result = new PluginVerifier().Verify(
            Manifest(),
            assembly,
            "deadbeef"
        );

        result.Verified.Should().BeFalse();
        result.Failures[0].Should().Contain(PluginRefusalCode.ChecksumSubjectMissing);
    }

    [Fact]
    public void No_published_checksum_still_installs_unverified()
    {
        string assembly = Write("NoMercy.Plugin.InternetRadio.dll", [5, 5]);

        PluginVerificationResult result = new PluginVerifier().Verify(Manifest(), assembly, null);

        result.Verified.Should().BeTrue();
        result.Trusted.Should().BeFalse();
    }
}
