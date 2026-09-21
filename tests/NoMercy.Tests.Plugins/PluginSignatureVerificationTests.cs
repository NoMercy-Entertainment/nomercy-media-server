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

using FluentAssertions;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Verification;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A checksum says the bytes did not change on the way; anyone who can serve
/// the file can serve a checksum for it. The signature is the only part that
/// answers who made it, which is the question that matters when the file is
/// about to run inside the server.
/// </summary>
public class PluginSignatureVerificationTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (string file in _files)
            try
            {
                File.Delete(file);
            }
            catch (Exception) { }
    }

    private static (string PublicKey, Ed25519PrivateKeyParameters Private) Keys()
    {
        Ed25519KeyPairGenerator generator = new();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        AsymmetricCipherKeyPair pair = generator.GenerateKeyPair();

        Ed25519PrivateKeyParameters privateKey = (Ed25519PrivateKeyParameters)pair.Private;
        Ed25519PublicKeyParameters publicKey = (Ed25519PublicKeyParameters)pair.Public;

        return (Convert.ToBase64String(publicKey.GetEncoded()), privateKey);
    }

    private static string Sign(byte[] content, Ed25519PrivateKeyParameters privateKey)
    {
        Ed25519Signer signer = new();
        signer.Init(true, privateKey);
        signer.BlockUpdate(content, 0, content.Length);

        return Convert.ToBase64String(signer.GenerateSignature());
    }

    private string WritePackage(byte[] content)
    {
        string path = Path.Combine(Path.GetTempPath(), "nomercy-pkg-" + Ulid.NewUlid() + ".zip");
        File.WriteAllBytes(path, content);
        _files.Add(path);

        return path;
    }

    private static PluginVerificationContext Context(
        string? packagePath,
        PluginSignatureBlock? signature,
        bool fromMarketplace = true
    ) =>
        new()
        {
            Manifest = new()
            {
                Id = new(Ulid.NewUlid()),
                Name = "Sample",
                Description = "d",
                Version = "1.0.0",
                TargetAbi = "11.0",
                Assembly = "Sample.dll",
            },
            AssemblyPath = "Sample.dll",
            PackagePath = packagePath,
            Signature = signature,
            FromMarketplace = fromMarketplace,
        };

    [Fact]
    public void A_package_signed_by_a_trusted_key_passes()
    {
        (string publicKey, Ed25519PrivateKeyParameters privateKey) = Keys();
        byte[] content = [1, 2, 3, 4, 5];
        string path = WritePackage(content);
        SignatureVerificationStage stage = new(
            new PluginTrustedKeys(new Dictionary<string, string> { ["k1"] = publicKey })
        );

        (PluginStageOutcome outcome, _) = stage.Evaluate(
            Context(path, new("ed25519", "k1", Sign(content, privateKey)))
        );

        outcome.Should().Be(PluginStageOutcome.Pass);
    }

    [Fact]
    public void A_package_changed_after_signing_fails()
    {
        (string publicKey, Ed25519PrivateKeyParameters privateKey) = Keys();
        string signature = Sign([1, 2, 3, 4, 5], privateKey);
        string path = WritePackage([1, 2, 3, 4, 6]);
        SignatureVerificationStage stage = new(
            new PluginTrustedKeys(new Dictionary<string, string> { ["k1"] = publicKey })
        );

        (PluginStageOutcome outcome, string? message) = stage.Evaluate(
            Context(path, new("ed25519", "k1", signature))
        );

        outcome.Should().Be(PluginStageOutcome.Fail);
        message.Should().Contain("does not match");
    }

    [Fact]
    public void A_signature_from_a_key_this_server_does_not_trust_fails()
    {
        (_, Ed25519PrivateKeyParameters privateKey) = Keys();
        (string otherPublic, _) = Keys();
        byte[] content = [9, 9, 9];
        string path = WritePackage(content);
        SignatureVerificationStage stage = new(
            new PluginTrustedKeys(new Dictionary<string, string> { ["k1"] = otherPublic })
        );

        (PluginStageOutcome outcome, string? message) = stage.Evaluate(
            Context(path, new("ed25519", "k1", Sign(content, privateKey)))
        );

        outcome
            .Should()
            .Be(
                PluginStageOutcome.Fail,
                "a valid signature by the wrong key is still a stranger's signature"
            );
        message.Should().Contain("does not match");
    }

    [Fact]
    public void A_key_id_this_server_has_never_seen_fails_and_says_so()
    {
        (string publicKey, Ed25519PrivateKeyParameters privateKey) = Keys();
        byte[] content = [7];
        string path = WritePackage(content);
        SignatureVerificationStage stage = new(
            new PluginTrustedKeys(new Dictionary<string, string> { ["k1"] = publicKey })
        );

        (PluginStageOutcome outcome, string? message) = stage.Evaluate(
            Context(path, new("ed25519", "unknown-key", Sign(content, privateKey)))
        );

        outcome.Should().Be(PluginStageOutcome.Fail);
        message.Should().Contain("does not trust");
    }

    [Fact]
    public void A_marketplace_package_with_no_signature_fails()
    {
        (string publicKey, _) = Keys();
        SignatureVerificationStage stage = new(
            new PluginTrustedKeys(new Dictionary<string, string> { ["k1"] = publicKey })
        );

        (PluginStageOutcome outcome, string? message) = stage.Evaluate(
            Context(WritePackage([1]), signature: null)
        );

        outcome.Should().Be(PluginStageOutcome.Fail);
        message.Should().Contain("without a signature");
    }

    [Fact]
    public void An_algorithm_this_server_does_not_read_fails_rather_than_being_ignored()
    {
        (string publicKey, _) = Keys();
        SignatureVerificationStage stage = new(
            new PluginTrustedKeys(new Dictionary<string, string> { ["k1"] = publicKey })
        );

        (PluginStageOutcome outcome, string? message) = stage.Evaluate(
            Context(WritePackage([1]), new("rsa-pss", "k1", "AAAA"))
        );

        outcome.Should().Be(PluginStageOutcome.Fail);
        message.Should().Contain("ed25519");
    }

    [Fact]
    public void A_package_that_is_no_longer_on_disk_fails_rather_than_throwing()
    {
        (string publicKey, Ed25519PrivateKeyParameters privateKey) = Keys();
        SignatureVerificationStage stage = new(
            new PluginTrustedKeys(new Dictionary<string, string> { ["k1"] = publicKey })
        );

        (PluginStageOutcome outcome, string? message) = stage.Evaluate(
            Context(
                Path.Combine(Path.GetTempPath(), "nomercy-pkg-gone-" + Ulid.NewUlid() + ".zip"),
                new("ed25519", "k1", Sign([1], privateKey))
            )
        );

        outcome.Should().Be(PluginStageOutcome.Fail);
        message.Should().Contain("no package");
    }

    [Fact]
    public void A_sideloaded_file_is_the_owners_own_decision()
    {
        (string publicKey, _) = Keys();
        SignatureVerificationStage stage = new(
            new PluginTrustedKeys(new Dictionary<string, string> { ["k1"] = publicKey })
        );

        (PluginStageOutcome outcome, _) = stage.Evaluate(
            Context(WritePackage([1]), signature: null, fromMarketplace: false)
        );

        outcome
            .Should()
            .Be(
                PluginStageOutcome.Pass,
                "refusing it would take away the one path that works while there is no marketplace"
            );
    }

    [Fact]
    public void With_no_keys_shipped_the_question_is_recorded_as_unanswered()
    {
        SignatureVerificationStage stage = new(PluginTrustedKeys.None);

        (PluginStageOutcome outcome, string? message) = stage.Evaluate(
            Context(WritePackage([1]), signature: null)
        );

        outcome
            .Should()
            .Be(
                PluginStageOutcome.Trust,
                "failing would take the marketplace offline the day it opened; passing silently would mean the stage never did anything"
            );
        message.Should().Contain("no publisher keys");
    }

    [Fact]
    public void The_stage_is_enforced_rather_than_advisory()
    {
        new SignatureVerificationStage()
            .Enforced.Should()
            .BeTrue("a stage nothing enforces is a stage that reports and is ignored");
    }

    [Theory]
    [InlineData("not base64 at all")]
    [InlineData("")]
    [InlineData("QUJD")]
    public void A_malformed_signature_is_false_rather_than_a_throw(string signature)
    {
        (string publicKey, _) = Keys();

        PluginEd25519
            .Verify([1, 2, 3], signature, publicKey)
            .Should()
            .BeFalse(
                "a throw for one shape and false for another tells an attacker which they got wrong"
            );
    }

    [Fact]
    public void A_malformed_public_key_is_false_rather_than_a_throw()
    {
        (_, Ed25519PrivateKeyParameters privateKey) = Keys();

        PluginEd25519.Verify([1, 2, 3], Sign([1, 2, 3], privateKey), "QUJD").Should().BeFalse();
    }
}
