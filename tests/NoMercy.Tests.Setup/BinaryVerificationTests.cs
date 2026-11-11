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
using System.Text;
using Newtonsoft.Json;
using NoMercy.Setup.Server;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace NoMercy.Tests.Setup;

[Trait("Category", "Unit")]
public class BinaryVerificationTests : IDisposable
{
    private readonly string _tempDir;

    public BinaryVerificationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nomercy-bv-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // -------------------------------------------------------------------------
    // SHA-256 verification
    // -------------------------------------------------------------------------

    [Fact]
    public async Task VerifyFileSha256_CorrectHash_ReturnsTrue()
    {
        byte[] content = Encoding.UTF8.GetBytes("hello nomercy");
        string filePath = Path.Combine(_tempDir, "test.bin");
        await File.WriteAllBytesAsync(filePath, content);

        string expected = Convert.ToHexString(SHA256.HashData(content));

        bool result = await BinaryVerification.VerifyFileSha256Async(filePath, expected);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyFileSha256_WrongHash_ReturnsFalse()
    {
        byte[] content = Encoding.UTF8.GetBytes("hello nomercy");
        string filePath = Path.Combine(_tempDir, "test.bin");
        await File.WriteAllBytesAsync(filePath, content);

        string wrong = new('0', 64);

        bool result = await BinaryVerification.VerifyFileSha256Async(filePath, wrong);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyFileSha256_HashCaseInsensitive_ReturnsTrue()
    {
        byte[] content = Encoding.UTF8.GetBytes("case test");
        string filePath = Path.Combine(_tempDir, "case.bin");
        await File.WriteAllBytesAsync(filePath, content);

        string expectedUpper = Convert.ToHexString(SHA256.HashData(content));
        string expectedLower = expectedUpper.ToLowerInvariant();

        bool upper = await BinaryVerification.VerifyFileSha256Async(filePath, expectedUpper);
        bool lower = await BinaryVerification.VerifyFileSha256Async(filePath, expectedLower);

        upper.Should().BeTrue();
        lower.Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // GitHub asset digest extraction
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("sha256:ABC123", "ABC123")]
    [InlineData("SHA256:deadbeef", "deadbeef")]
    public void ExtractSha256FromDigest_ValidPrefix_ReturnsHex(string digest, string expected)
    {
        BinaryVerification.ExtractSha256FromDigest(digest).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sha512:abcdef")]
    [InlineData("abcdef")]
    public void ExtractSha256FromDigest_MissingOrUnsupported_ReturnsNull(string? digest)
    {
        BinaryVerification.ExtractSha256FromDigest(digest).Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // Upstream SHA2-256SUMS parsing (yt-dlp and friends)
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseSha256Sums_FindsMatchingAsset()
    {
        string sums = "aaa111  yt-dlp\nbbb222  yt-dlp_linux\nccc333  yt-dlp.exe\n";

        BinaryVerification.ParseSha256Sums(sums, "yt-dlp_linux").Should().Be("bbb222");
    }

    [Fact]
    public void ParseSha256Sums_ToleratesBinaryMarkerAndBlankLines()
    {
        string sums = "\n   \ndeadbeef *yt-dlp_macos\n";

        BinaryVerification.ParseSha256Sums(sums, "yt-dlp_macos").Should().Be("deadbeef");
    }

    // -------------------------------------------------------------------------
    // Per-asset .sha256 sidecars, in the two forms releases publish them
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("deadbeef\n", "deadbeef")]
    [InlineData("  deadbeef  \r\n", "deadbeef")]
    [InlineData("deadbeef *model.gguf\n", "deadbeef")]
    [InlineData("deadbeef  model.gguf\n", "deadbeef")]
    [InlineData("deadbeef *MODEL.GGUF", "deadbeef")]
    public void ReadSha256Sidecar_ReadsTheDigest_BareOrAsASha256SumLine(
        string sidecar,
        string digest
    )
    {
        BinaryVerification.ReadSha256Sidecar(sidecar, "model.gguf").Should().Be(digest);
    }

    /// <summary>
    /// A sidecar that describes another file is not turned into a digest for
    /// this one. What comes back is not a digest at all, so the verification
    /// that follows fails instead of passing on somebody else's checksum.
    /// </summary>
    [Fact]
    public void ReadSha256Sidecar_ForAnotherFile_YieldsNothingThatCanVerify()
    {
        string read = BinaryVerification.ReadSha256Sidecar(
            "deadbeef *another.gguf\n",
            "model.gguf"
        );

        read.Should().Be("deadbeef *another.gguf");
        read.Should().NotBe("deadbeef");
    }

    [Fact]
    public void ParseSha256Sums_NoMatch_ReturnsNull()
    {
        string sums = "aaa111  yt-dlp\nbbb222  yt-dlp_linux\n";

        BinaryVerification.ParseSha256Sums(sums, "yt-dlp_win.exe").Should().BeNull();
    }

    [Fact]
    public void ParseSha256Sums_Empty_ReturnsNull()
    {
        BinaryVerification.ParseSha256Sums(string.Empty, "anything").Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // Manifest JSON parsing
    // -------------------------------------------------------------------------

    [Fact]
    public void ReleaseManifest_Deserializes_AllFields()
    {
        string json = """
            {
              "version": "1.2.3",
              "commit_sha": "abc1234",
              "build_timestamp": "2025-01-01T00:00:00Z",
              "assets": [
                { "name": "nomercy-linux-x64", "sha256": "deadbeef", "size": 42 }
              ]
            }
            """;

        ReleaseManifest? manifest = JsonConvert.DeserializeObject<ReleaseManifest>(json);

        manifest.Should().NotBeNull();
        manifest!.Version.Should().Be("1.2.3");
        manifest.CommitSha.Should().Be("abc1234");
        manifest.Assets.Should().HaveCount(1);
        manifest.Assets[0].Name.Should().Be("nomercy-linux-x64");
        manifest.Assets[0].Sha256.Should().Be("deadbeef");
        manifest.Assets[0].Size.Should().Be(42);
    }

    [Fact]
    public void ReleaseManifest_EmptyAssets_ParsesWithoutError()
    {
        string json = """{"version":"0.1","commit_sha":"x","build_timestamp":"t","assets":[]}""";

        ReleaseManifest? manifest = JsonConvert.DeserializeObject<ReleaseManifest>(json);

        manifest.Should().NotBeNull();
        manifest!.Assets.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // PGP manifest signature verification
    // -------------------------------------------------------------------------

    [Fact]
    public void VerifyManifestSignature_GarbageSignatureAgainstEmbeddedKey_ReturnsFalse()
    {
        // The public overload uses the real embedded org key. A malformed detached
        // signature must be rejected cleanly (false), never throw.
        bool result = BinaryVerification.VerifyManifestSignature(
            """{"version":"1.0"}""",
            "-----BEGIN PGP SIGNATURE-----\nfake\n-----END PGP SIGNATURE-----"
        );

        result.Should().BeFalse();
    }

    [Fact]
    public void VerifyManifestSignature_ForeignSignatureAgainstEmbeddedKey_ReturnsFalse()
    {
        string manifest = """{"version":"1.0","assets":[]}""";
        (_, string foreignSignature) = GenerateSignedManifest(manifest);

        // A valid signature from a key that is NOT the embedded org key must not
        // verify — the embedded overload looks the signer up by key id and misses.
        bool result = BinaryVerification.VerifyManifestSignature(manifest, foreignSignature);

        result.Should().BeFalse();
    }

    [Fact]
    public void EmbeddedPublicKey_IsRealKey_NotPlaceholder()
    {
        // Regression guard: the shipped assembly must embed the real org public key,
        // not the placeholder that silently disabled all signature verification.
        using Stream? stream = typeof(BinaryVerification).Assembly.GetManifestResourceStream(
            "NoMercy.Setup.Resources.nomercy-public-key.asc"
        );

        stream.Should().NotBeNull("the org public key must be embedded for manifest verification");

        using StreamReader reader = new(stream!);
        string content = reader.ReadToEnd();

        content.Should().Contain("BEGIN PGP PUBLIC KEY BLOCK");
        content.Should().NotContain("PLACEHOLDER");
    }

    [Fact]
    public void VerifyManifestSignature_ValidKeyAndSignature_ReturnsTrue()
    {
        string manifest = """{"version":"1.0","assets":[]}""";

        (string armoredPublicKey, string armoredSignature) = GenerateSignedManifest(manifest);

        bool result = BinaryVerification.VerifyManifestSignature(
            manifest,
            armoredSignature,
            armoredPublicKey
        );

        result.Should().BeTrue();
    }

    [Fact]
    public void VerifyManifestSignature_TamperedManifest_ReturnsFalse()
    {
        string originalManifest = """{"version":"1.0","assets":[]}""";
        string tamperedManifest = """{"version":"9.9","assets":[]}""";

        (string armoredPublicKey, string armoredSignature) = GenerateSignedManifest(
            originalManifest
        );

        bool result = BinaryVerification.VerifyManifestSignature(
            tamperedManifest,
            armoredSignature,
            armoredPublicKey
        );

        result.Should().BeFalse();
    }

    [Fact]
    public void VerifyManifestSignature_WrongPublicKey_ReturnsFalse()
    {
        string manifest = """{"version":"1.0","assets":[]}""";

        (_, string armoredSignature) = GenerateSignedManifest(manifest);
        (string differentPublicKey, _) = GenerateSignedManifest(manifest);

        bool result = BinaryVerification.VerifyManifestSignature(
            manifest,
            armoredSignature,
            differentPublicKey
        );

        result.Should().BeFalse();
    }

    [Fact]
    public void VerifyManifestSignature_GarbageSignature_ReturnsFalse()
    {
        string manifest = """{"version":"1.0","assets":[]}""";
        (string armoredPublicKey, _) = GenerateSignedManifest(manifest);

        bool result = BinaryVerification.VerifyManifestSignature(
            manifest,
            "this is not a valid pgp signature",
            armoredPublicKey
        );

        result.Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Generates a fresh RSA PGP key pair, signs <paramref name="manifest"/> with the
    /// private key, and returns the ASCII-armored public key and detached signature.
    /// </summary>
    private static (string ArmoredPublicKey, string ArmoredSignature) GenerateSignedManifest(
        string manifest
    )
    {
        IAsymmetricCipherKeyPairGenerator keyGen = GeneratorUtilities.GetKeyPairGenerator("RSA");
        keyGen.Init(
            new RsaKeyGenerationParameters(
                Org.BouncyCastle.Math.BigInteger.ValueOf(0x10001),
                new(),
                2048,
                12
            )
        );
        AsymmetricCipherKeyPair keyPair = keyGen.GenerateKeyPair();

        PgpKeyPair pgpKeyPair = new(PublicKeyAlgorithmTag.RsaGeneral, keyPair, DateTime.UtcNow);

        // Export armored public key
        string armoredPublicKey;
        using (MemoryStream pubOut = new())
        {
            using ArmoredOutputStream armoredPub = new(pubOut);
            pgpKeyPair.PublicKey.Encode(armoredPub);
            armoredPub.Close();
            armoredPublicKey = Encoding.ASCII.GetString(pubOut.ToArray());
        }

        // Wrap public key in a ring so VerifyManifestSignature can look up by key ID
        PgpPublicKeyRing publicKeyRing = new(pgpKeyPair.PublicKey.GetEncoded());
        _ = publicKeyRing; // validated by VerifyManifestSignature via key-ID lookup

        // Create detached signature
        PgpSignatureGenerator sigGen = new(
            PublicKeyAlgorithmTag.RsaGeneral,
            HashAlgorithmTag.Sha256
        );
        sigGen.InitSign(PgpSignature.BinaryDocument, pgpKeyPair.PrivateKey);

        byte[] data = Encoding.UTF8.GetBytes(manifest);
        sigGen.Update(data, 0, data.Length);

        PgpSignature sig = sigGen.Generate();

        string armoredSignature;
        using (MemoryStream sigOut = new())
        {
            using ArmoredOutputStream armoredSig = new(sigOut);
            sig.Encode(armoredSig);
            armoredSig.Close();
            armoredSignature = Encoding.ASCII.GetString(sigOut.ToArray());
        }

        return (armoredPublicKey, armoredSignature);
    }
}
