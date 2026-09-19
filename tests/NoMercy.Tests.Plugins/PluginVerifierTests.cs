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
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Verification;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginVerifierTests
{
    private static string WriteTempDll(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"plugin-{Ulid.NewUlid():N}.dll");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string WriteTempPackage(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"plugin-{Ulid.NewUlid():N}.zip");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static PluginManifest Manifest(string? abi) =>
        new()
        {
            Id = Ulid.NewUlid(),
            Name = "n",
            Description = "d",
            Version = "1.0.0",
            Assembly = "x.dll",
            TargetAbi = abi,
        };

    [Fact]
    public void Verify_AbiMismatch_FailsAndNotVerified()
    {
        string dll = WriteTempDll([1, 2, 3]);
        PluginVerifier verifier = new();
        PluginVerificationResult result = verifier.Verify(Manifest("12.0"), dll, null);
        Assert.False(result.Verified);
        Assert.Contains(result.Failures, f => f.Contains("ABI"));
    }

    [Fact]
    public void Verify_ChecksumMatch_TrustedAndVerified()
    {
        byte[] bytes = [10, 20, 30, 40];
        string package = WriteTempPackage(bytes);
        string dll = WriteTempDll([1, 1, 1]);
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        PluginVerifier verifier = new();
        PluginVerificationResult result = verifier.Verify(Manifest("10.0"), dll, sha, package);
        Assert.True(result.Verified);
        Assert.True(result.Trusted);
    }

    [Fact]
    public void Verify_ChecksumMismatch_NotVerified()
    {
        string package = WriteTempPackage([1, 1, 1]);
        string dll = WriteTempDll([1, 1, 1]);
        PluginVerifier verifier = new();
        PluginVerificationResult result = verifier.Verify(
            Manifest("10.0"),
            dll,
            "deadbeef",
            package
        );
        Assert.False(result.Verified);
        Assert.Contains(result.Failures, f => f.Contains("checksum"));
    }

    [Fact]
    public void Verify_NoExpectedChecksum_VerifiedButNotTrusted()
    {
        string dll = WriteTempDll([5, 5]);
        PluginVerifier verifier = new();
        PluginVerificationResult result = verifier.Verify(Manifest("10.0"), dll, null);
        Assert.True(result.Verified);
        Assert.False(result.Trusted);
    }

    [Fact]
    public void Verify_NonEnforcedStageFails_DoesNotFailOverallVerification()
    {
        // A stage that is NOT enforced (Enforced == false) must never contribute
        // its failure to the verdict — the `stage.Enforced` half of the
        // `outcome == Fail && stage.Enforced` guard exists specifically to keep
        // an advisory-only stage from ever blocking a plugin load.
        string dll = WriteTempDll([1, 2, 3]);
        PluginVerifier verifier = new([new AdvisoryFailingStage()]);

        PluginVerificationResult result = verifier.Verify(Manifest("10.0"), dll, null);

        Assert.True(result.Verified);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void Verify_EnforcedStageFailsWithNullMessage_FallsBackToStageNameMessage()
    {
        // Every other failing-stage test (Abi, Checksum) supplies a real
        // message — this is the only path that reaches the
        // `message ?? $"{stage.Name} stage failed."` fallback on the right of
        // the `??`.
        string dll = WriteTempDll([9, 9, 9]);
        PluginVerifier verifier = new([new EnforcedFailingStageWithNoMessage()]);

        PluginVerificationResult result = verifier.Verify(Manifest("10.0"), dll, null);

        Assert.False(result.Verified);
        Assert.Equal("NoMessage stage failed.", Assert.Single(result.Failures));
    }

    [Fact]
    public void AbiVerificationStage_ExposesNameAndEnforced()
    {
        AbiVerificationStage stage = new();

        Assert.Equal("ABI", stage.Name);
        Assert.True(stage.Enforced);
    }

    [Fact]
    public void ChecksumVerificationStage_ExposesNameAndEnforced()
    {
        ChecksumVerificationStage stage = new();

        Assert.Equal("Checksum", stage.Name);
        Assert.True(stage.Enforced);
    }

    [Fact]
    public void SignatureVerificationStage_ExposesNameAndIsNotEnforced()
    {
        SignatureVerificationStage stage = new();

        Assert.Equal("Signature", stage.Name);
        Assert.False(stage.Enforced);
    }

    [Fact]
    public void SignatureVerificationStage_Evaluate_AlwaysPasses()
    {
        SignatureVerificationStage stage = new();
        PluginVerificationContext context = new()
        {
            Manifest = Manifest("10.0"),
            AssemblyPath = "irrelevant",
            ExpectedChecksum = null,
        };

        (PluginStageOutcome outcome, string? message) = stage.Evaluate(context);

        Assert.Equal(PluginStageOutcome.Pass, outcome);
        Assert.Null(message);
    }

    private sealed class AdvisoryFailingStage : IPluginVerificationStage
    {
        public string Name => "Advisory";
        public bool Enforced => false;

        public (PluginStageOutcome Outcome, string? Message) Evaluate(
            PluginVerificationContext context
        ) => (PluginStageOutcome.Fail, "advisory failure, never enforced");
    }

    private sealed class EnforcedFailingStageWithNoMessage : IPluginVerificationStage
    {
        public string Name => "NoMessage";
        public bool Enforced => true;

        public (PluginStageOutcome Outcome, string? Message) Evaluate(
            PluginVerificationContext context
        ) => (PluginStageOutcome.Fail, null);
    }
}
