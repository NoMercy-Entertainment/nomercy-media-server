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

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using NoMercy.Setup.Dto;
using NoMercy.Setup.Server;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;

namespace NoMercy.Tests.Setup;

/// <summary>
/// Tests for the download-and-verify pipeline in <see cref="Binaries"/>.
/// Uses a stub <see cref="HttpMessageHandler"/> so no network access is required.
/// File I/O is performed against the real filesystem in a per-test temp directory.
/// </summary>
[Trait("Category", "Unit")]
public class BinaryDownloaderTests : IDisposable
{
    private readonly string _tempDir;

    public BinaryDownloaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nomercy-dl-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // -------------------------------------------------------------------------
    // Happy path: no SHA-256 sidecar, no manifest → accepts file as-is
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Download_NoChecksumAvailable_SucceedsAndWritesFile()
    {
        byte[] payload = Encoding.UTF8.GetBytes("binary content");
        string destPath = Path.Combine(_tempDir, "asset.bin");
        string assetUrl = "https://example.com/asset.bin";

        GithubReleaseResponse release = BuildRelease(
            assetName: "asset.bin",
            assetUrl: assetUrl,
            sha256: null,
            includeManifest: false
        );

        FakeHttpHandler handler = new();
        handler.Register(assetUrl, payload);
        handler.RegisterRelease(release);

        Binaries binaries = BuildBinaries(handler);

        string result = await binaries.DownloadWithVerificationAsync(
            "https://api.github.com/test",
            "asset",
            new(assetUrl),
            destPath,
            release,
            "asset.bin",
            enforceSignedManifest: false
        );

        result.Should().Be(destPath);
        File.Exists(destPath).Should().BeTrue();
        (await File.ReadAllBytesAsync(destPath)).Should().BeEquivalentTo(payload);
    }

    // -------------------------------------------------------------------------
    // SHA-256 sidecar: matching hash → accepts file
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Download_MatchingSha256Sidecar_SucceedsAndWritesFile()
    {
        byte[] payload = Encoding.UTF8.GetBytes("verified binary");
        string sha256 = Convert.ToHexString(SHA256.HashData(payload));
        string destPath = Path.Combine(_tempDir, "asset.bin");
        string assetUrl = "https://example.com/asset.bin";
        string sha256Url = assetUrl + ".sha256";

        GithubReleaseResponse release = BuildRelease(
            assetName: "asset.bin",
            assetUrl: assetUrl,
            sha256: sha256,
            sha256Url: sha256Url,
            includeManifest: false
        );

        FakeHttpHandler handler = new();
        handler.Register(assetUrl, payload);
        handler.Register(sha256Url, Encoding.ASCII.GetBytes(sha256));
        handler.RegisterRelease(release);

        Binaries binaries = BuildBinaries(handler);

        string result = await binaries.DownloadWithVerificationAsync(
            "https://api.github.com/test",
            "asset",
            new(assetUrl),
            destPath,
            release,
            "asset.bin",
            enforceSignedManifest: false
        );

        result.Should().Be(destPath);
        (await File.ReadAllBytesAsync(destPath)).Should().BeEquivalentTo(payload);
    }

    // -------------------------------------------------------------------------
    // SHA-256 sidecar: mismatched hash → throws InvalidDataException
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Download_MismatchedSha256Sidecar_Throws()
    {
        byte[] payload = Encoding.UTF8.GetBytes("tampered binary");
        string wrongHash = new('a', 64);
        string destPath = Path.Combine(_tempDir, "asset.bin");
        string assetUrl = "https://example.com/asset.bin";
        string sha256Url = assetUrl + ".sha256";

        GithubReleaseResponse release = BuildRelease(
            assetName: "asset.bin",
            assetUrl: assetUrl,
            sha256: wrongHash,
            sha256Url: sha256Url,
            includeManifest: false
        );

        FakeHttpHandler handler = new();
        handler.Register(assetUrl, payload);
        handler.Register(sha256Url, Encoding.ASCII.GetBytes(wrongHash));
        handler.RegisterRelease(release);

        Binaries binaries = BuildBinaries(handler);

        Func<Task> act = () =>
            binaries.DownloadWithVerificationAsync(
                "https://api.github.com/test",
                "asset",
                new(assetUrl),
                destPath,
                release,
                "asset.bin",
                enforceSignedManifest: false
            );

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*SHA-256 mismatch*");

        // temp file must be cleaned up
        File.Exists(destPath + ".tmp").Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Manifest: SHA-256 from manifest, matching → accepts file
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Download_MatchingSha256FromManifest_Succeeds()
    {
        byte[] payload = Encoding.UTF8.GetBytes("manifest-verified binary");
        string sha256 = Convert.ToHexString(SHA256.HashData(payload));
        string destPath = Path.Combine(_tempDir, "asset.bin");
        string assetUrl = "https://example.com/asset.bin";
        string manifestUrl = "https://example.com/manifest.json";

        ReleaseManifest manifest = new()
        {
            Version = "1.0",
            CommitSha = "abc",
            BuildTimestamp = "2025-01-01T00:00:00Z",
            Assets =
            [
                new()
                {
                    Name = "asset.bin",
                    Sha256 = sha256,
                    Size = payload.Length,
                },
            ],
        };
        string manifestJson = JsonConvert.SerializeObject(manifest);

        GithubReleaseResponse release = BuildRelease(
            assetName: "asset.bin",
            assetUrl: assetUrl,
            sha256: null,
            includeManifest: true,
            manifestUrl: manifestUrl
        );

        FakeHttpHandler handler = new();
        handler.Register(assetUrl, payload);
        handler.Register(manifestUrl, Encoding.UTF8.GetBytes(manifestJson));
        handler.RegisterRelease(release);

        Binaries binaries = BuildBinaries(handler);

        string result = await binaries.DownloadWithVerificationAsync(
            "https://api.github.com/test",
            "asset",
            new(assetUrl),
            destPath,
            release,
            "asset.bin",
            enforceSignedManifest: false
        );

        result.Should().Be(destPath);
        (await File.ReadAllBytesAsync(destPath)).Should().BeEquivalentTo(payload);
    }

    // -------------------------------------------------------------------------
    // Atomic swap: existing file is backed up, restored on failure
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Download_ExistingFileBackedUp_RestoredOnHashMismatch()
    {
        byte[] originalContent = Encoding.UTF8.GetBytes("original content");
        byte[] newPayload = Encoding.UTF8.GetBytes("new binary that fails sha");
        string wrongHash = new('b', 64);
        string destPath = Path.Combine(_tempDir, "asset.bin");
        string assetUrl = "https://example.com/asset.bin";
        string sha256Url = assetUrl + ".sha256";

        await File.WriteAllBytesAsync(destPath, originalContent);

        GithubReleaseResponse release = BuildRelease(
            assetName: "asset.bin",
            assetUrl: assetUrl,
            sha256: wrongHash,
            sha256Url: sha256Url,
            includeManifest: false
        );

        FakeHttpHandler handler = new();
        handler.Register(assetUrl, newPayload);
        handler.Register(sha256Url, Encoding.ASCII.GetBytes(wrongHash));
        handler.RegisterRelease(release);

        Binaries binaries = BuildBinaries(handler);

        Func<Task> act = () =>
            binaries.DownloadWithVerificationAsync(
                "https://api.github.com/test",
                "asset",
                new(assetUrl),
                destPath,
                release,
                "asset.bin",
                enforceSignedManifest: false
            );

        await act.Should().ThrowAsync<InvalidDataException>();

        // Original file must still be present (was not overwritten before verification)
        File.Exists(destPath).Should().BeTrue();
        (await File.ReadAllBytesAsync(destPath)).Should().BeEquivalentTo(originalContent);
    }

    // -------------------------------------------------------------------------
    // Trust policy: enforcement for owned assets + the GitHub digest integrity floor
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Download_OwnedRepo_ManifestSignatureInvalid_FallsBackToDigest_Succeeds()
    {
        // A signature that does not verify (e.g. the embedded key is stale after a key
        // rotation) must NOT brick the update path. The unverified manifest's hash is not
        // trusted — even though it is present, the download is governed by GitHub's
        // independent asset digest, and succeeds when that matches.
        byte[] payload = Encoding.UTF8.GetBytes("owned payload after rotation");
        string correctDigest =
            "sha256:" + Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        string destPath = Path.Combine(_tempDir, "asset.bin");
        string assetUrl = "https://example.com/asset.bin";
        string manifestUrl = "https://example.com/manifest.json";
        string manifestSigUrl = "https://example.com/manifest.json.sig";

        // Manifest hash is deliberately WRONG: if the unverified manifest were trusted
        // this would fail, proving the fallback ignores it and uses the digest instead.
        ReleaseManifest manifest = new()
        {
            Version = "1.0",
            Assets =
            [
                new()
                {
                    Name = "asset.bin",
                    Sha256 = new('e', 64),
                    Size = payload.Length,
                },
            ],
        };

        GithubReleaseResponse release = BuildRelease(
            assetName: "asset.bin",
            assetUrl: assetUrl,
            sha256: null,
            includeManifest: true,
            manifestUrl: manifestUrl,
            manifestSigUrl: manifestSigUrl,
            digest: correctDigest
        );

        FakeHttpHandler handler = new();
        handler.Register(assetUrl, payload);
        handler.Register(
            manifestUrl,
            Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(manifest))
        );
        handler.Register(
            manifestSigUrl,
            Encoding.ASCII.GetBytes(
                "-----BEGIN PGP SIGNATURE-----\nnot-a-real-signature\n-----END PGP SIGNATURE-----"
            )
        );

        Binaries binaries = BuildBinaries(handler);

        string result = await binaries.DownloadWithVerificationAsync(
            "https://api.github.com/test",
            "asset",
            new(assetUrl),
            destPath,
            release,
            "asset.bin",
            enforceSignedManifest: true
        );

        result.Should().Be(destPath);
        (await File.ReadAllBytesAsync(destPath)).Should().BeEquivalentTo(payload);
    }

    [Fact]
    public async Task Download_OwnedRepo_ManifestSignatureInvalid_DigestMismatch_Throws()
    {
        // When the signature is bypassed, the digest integrity floor still applies —
        // bytes that don't match GitHub's digest are rejected, so a bad signature never
        // opens the door to a corrupted or truncated download.
        byte[] payload = Encoding.UTF8.GetBytes("tampered owned payload");
        string destPath = Path.Combine(_tempDir, "asset.bin");
        string assetUrl = "https://example.com/asset.bin";
        string manifestUrl = "https://example.com/manifest.json";
        string manifestSigUrl = "https://example.com/manifest.json.sig";
        string wrongDigest = "sha256:" + new string('f', 64);

        ReleaseManifest manifest = new()
        {
            Version = "1.0",
            Assets =
            [
                new()
                {
                    Name = "asset.bin",
                    Sha256 = Convert.ToHexString(SHA256.HashData(payload)),
                    Size = payload.Length,
                },
            ],
        };

        GithubReleaseResponse release = BuildRelease(
            assetName: "asset.bin",
            assetUrl: assetUrl,
            sha256: null,
            includeManifest: true,
            manifestUrl: manifestUrl,
            manifestSigUrl: manifestSigUrl,
            digest: wrongDigest
        );

        FakeHttpHandler handler = new();
        handler.Register(assetUrl, payload);
        handler.Register(
            manifestUrl,
            Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(manifest))
        );
        handler.Register(
            manifestSigUrl,
            Encoding.ASCII.GetBytes(
                "-----BEGIN PGP SIGNATURE-----\nnot-a-real-signature\n-----END PGP SIGNATURE-----"
            )
        );

        Binaries binaries = BuildBinaries(handler);

        Func<Task> act = () =>
            binaries.DownloadWithVerificationAsync(
                "https://api.github.com/test",
                "asset",
                new(assetUrl),
                destPath,
                release,
                "asset.bin",
                enforceSignedManifest: true
            );

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*SHA-256 mismatch*");
    }

    [Fact]
    public async Task Download_OwnedRepo_NoManifest_FallsBackToDigest_Succeeds()
    {
        // Rollout tolerance: an owned release with no manifest yet is still accepted,
        // integrity-checked against GitHub's asset digest rather than hard-failing.
        byte[] payload = Encoding.UTF8.GetBytes("owned no-manifest payload");
        string digest =
            "sha256:" + Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        string destPath = Path.Combine(_tempDir, "asset.bin");
        string assetUrl = "https://example.com/asset.bin";

        GithubReleaseResponse release = BuildRelease(
            assetName: "asset.bin",
            assetUrl: assetUrl,
            sha256: null,
            digest: digest
        );

        FakeHttpHandler handler = new();
        handler.Register(assetUrl, payload);

        Binaries binaries = BuildBinaries(handler);

        string result = await binaries.DownloadWithVerificationAsync(
            "https://api.github.com/test",
            "asset",
            new(assetUrl),
            destPath,
            release,
            "asset.bin",
            enforceSignedManifest: true
        );

        result.Should().Be(destPath);
        (await File.ReadAllBytesAsync(destPath)).Should().BeEquivalentTo(payload);
    }

    [Fact]
    public async Task Download_OwnedRepo_ManifestPresentNoSignatureAsset_FallsBackToDigest_Succeeds()
    {
        // Reproduces the real nomercy-ffmpeg v1.0.38 release shape: manifest.json is
        // published but manifest.json.sig is not (the CI signing rollout has not
        // reached that repo yet). This must be treated as "no signature to verify
        // yet" and fall back to the GitHub digest cleanly — not as a signature that
        // was checked and failed.
        byte[] payload = Encoding.UTF8.GetBytes("ffmpeg-shaped payload, unsigned manifest");
        string digest =
            "sha256:" + Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        string destPath = Path.Combine(_tempDir, "asset.bin");
        string assetUrl = "https://example.com/asset.bin";
        string manifestUrl = "https://example.com/manifest.json";

        // Manifest hash is deliberately WRONG: an untrusted (unsigned) manifest must
        // never be relied on for the hash, proving the digest fallback is what
        // actually governs acceptance here.
        ReleaseManifest manifest = new()
        {
            Version = "1.0.38",
            Assets =
            [
                new()
                {
                    Name = "asset.bin",
                    Sha256 = new('d', 64),
                    Size = payload.Length,
                },
            ],
        };

        GithubReleaseResponse release = BuildRelease(
            assetName: "asset.bin",
            assetUrl: assetUrl,
            sha256: null,
            includeManifest: true,
            manifestUrl: manifestUrl,
            manifestSigUrl: null,
            digest: digest
        );

        FakeHttpHandler handler = new();
        handler.Register(assetUrl, payload);
        handler.Register(
            manifestUrl,
            Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(manifest))
        );

        Binaries binaries = BuildBinaries(handler);

        string result = await binaries.DownloadWithVerificationAsync(
            "https://api.github.com/test",
            "FFMpeg",
            new(assetUrl),
            destPath,
            release,
            "asset.bin",
            enforceSignedManifest: true
        );

        result.Should().Be(destPath);
        (await File.ReadAllBytesAsync(destPath)).Should().BeEquivalentTo(payload);
    }

    // -------------------------------------------------------------------------
    // Empty/incomplete download: must abort before the file ever reaches the
    // extraction step, not just log a warning and carry on.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Download_EmptyPayload_ThrowsAndLeavesNoTempOrDestFile()
    {
        // A response that completes with zero bytes (truncated connection,
        // interrupted CDN transfer) must never leave a file behind for a later
        // extraction step to pick up — this is the exact "extraction ran against
        // a file that isn't really there" failure mode.
        string destPath = Path.Combine(_tempDir, "asset.bin");
        string assetUrl = "https://example.com/asset.bin";

        GithubReleaseResponse release = BuildRelease("asset.bin", assetUrl, sha256: null);

        FakeHttpHandler handler = new();
        handler.Register(assetUrl, []);

        Binaries binaries = BuildBinaries(handler);

        Func<Task> act = () =>
            binaries.DownloadWithVerificationAsync(
                "https://api.github.com/test",
                "asset",
                new(assetUrl),
                destPath,
                release,
                "asset.bin",
                enforceSignedManifest: false
            );

        await act.Should().ThrowAsync<IOException>().WithMessage("*empty or missing*");

        File.Exists(destPath).Should().BeFalse();
        File.Exists(destPath + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task Download_DigestMismatch_Throws()
    {
        // Universal integrity floor: bytes that don't match GitHub's asset digest are
        // rejected even for a third-party dep with no manifest.
        byte[] payload = Encoding.UTF8.GetBytes("third-party payload");
        string wrongDigest = "sha256:" + new string('a', 64);
        string destPath = Path.Combine(_tempDir, "asset.bin");
        string assetUrl = "https://example.com/asset.bin";

        GithubReleaseResponse release = BuildRelease(
            assetName: "asset.bin",
            assetUrl: assetUrl,
            sha256: null,
            digest: wrongDigest
        );

        FakeHttpHandler handler = new();
        handler.Register(assetUrl, payload);

        Binaries binaries = BuildBinaries(handler);

        Func<Task> act = () =>
            binaries.DownloadWithVerificationAsync(
                "https://api.github.com/test",
                "asset",
                new(assetUrl),
                destPath,
                release,
                "asset.bin",
                enforceSignedManifest: false
            );

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*SHA-256 mismatch*");
    }

    [Fact]
    public async Task Download_ExpectedSha256Override_Match_Succeeds()
    {
        // yt-dlp path: the caller supplies the hash from the upstream SHA2-256SUMS.
        byte[] payload = Encoding.UTF8.GetBytes("yt-dlp binary");
        string sha256 = Convert.ToHexString(SHA256.HashData(payload));
        string destPath = Path.Combine(_tempDir, "asset.bin");
        string assetUrl = "https://example.com/asset.bin";

        GithubReleaseResponse release = BuildRelease("asset.bin", assetUrl, sha256: null);

        FakeHttpHandler handler = new();
        handler.Register(assetUrl, payload);

        Binaries binaries = BuildBinaries(handler);

        string result = await binaries.DownloadWithVerificationAsync(
            "https://api.github.com/test",
            "yt-dlp",
            new(assetUrl),
            destPath,
            release,
            "asset.bin",
            enforceSignedManifest: false,
            expectedSha256Override: sha256
        );

        result.Should().Be(destPath);
    }

    [Fact]
    public async Task Download_ExpectedSha256Override_Mismatch_Throws()
    {
        byte[] payload = Encoding.UTF8.GetBytes("yt-dlp binary");
        string wrong = new('c', 64);
        string destPath = Path.Combine(_tempDir, "asset.bin");
        string assetUrl = "https://example.com/asset.bin";

        GithubReleaseResponse release = BuildRelease("asset.bin", assetUrl, sha256: null);

        FakeHttpHandler handler = new();
        handler.Register(assetUrl, payload);

        Binaries binaries = BuildBinaries(handler);

        Func<Task> act = () =>
            binaries.DownloadWithVerificationAsync(
                "https://api.github.com/test",
                "yt-dlp",
                new(assetUrl),
                destPath,
                release,
                "asset.bin",
                enforceSignedManifest: false,
                expectedSha256Override: wrong
            );

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*SHA-256 mismatch*");
    }

    // -------------------------------------------------------------------------
    // PGP manifest signature verification (hermetic: keypair generated in-test)
    // -------------------------------------------------------------------------

    private const string SignedManifestJson =
        """{"version":"1.0.0","commit_sha":"abc123","build_timestamp":"2026-07-04T00:00:00Z","assets":[]}""";

    [Fact]
    public void VerifyManifestSignature_SignedManifest_VerifiesCorrectly()
    {
        (string armoredPublicKey, PgpSecretKey secretKey) = GeneratePgpKeyPair();
        string armoredSignature = SignDetachedArmored(SignedManifestJson, secretKey);

        bool verified = BinaryVerification.VerifyManifestSignature(
            SignedManifestJson,
            armoredSignature,
            armoredPublicKey
        );

        verified.Should().BeTrue("a manifest signed by the supplied key must verify against it");
    }

    [Fact]
    public void VerifyManifestSignature_TamperedManifest_FailsVerification()
    {
        (string armoredPublicKey, PgpSecretKey secretKey) = GeneratePgpKeyPair();
        string armoredSignature = SignDetachedArmored(SignedManifestJson, secretKey);

        string tampered = SignedManifestJson.Replace("1.0.0", "6.6.6");

        bool verified = BinaryVerification.VerifyManifestSignature(
            tampered,
            armoredSignature,
            armoredPublicKey
        );

        verified.Should().BeFalse("any change to the manifest must invalidate the signature");
    }

    [Fact]
    public void VerifyManifestSignature_WrongKey_FailsVerification()
    {
        (string _, PgpSecretKey signingKey) = GeneratePgpKeyPair();
        (string unrelatedPublicKey, PgpSecretKey _) = GeneratePgpKeyPair();
        string armoredSignature = SignDetachedArmored(SignedManifestJson, signingKey);

        bool verified = BinaryVerification.VerifyManifestSignature(
            SignedManifestJson,
            armoredSignature,
            unrelatedPublicKey
        );

        verified.Should().BeFalse("a signature must not verify against an unrelated public key");
    }

    private static (string ArmoredPublicKey, PgpSecretKey SecretKey) GeneratePgpKeyPair()
    {
        RsaKeyPairGenerator generator = new();
        generator.Init(new(new(), 2048));
        AsymmetricCipherKeyPair keyPair = generator.GenerateKeyPair();

        PgpSecretKey secretKey = new(
            PgpSignature.DefaultCertification,
            PublicKeyAlgorithmTag.RsaGeneral,
            keyPair.Public,
            keyPair.Private,
            DateTime.UtcNow,
            "test@nomercy.tv",
            SymmetricKeyAlgorithmTag.Aes256,
            "test-passphrase".ToCharArray(),
            null,
            null,
            new()
        );

        using MemoryStream publicKeyOut = new();
        using (ArmoredOutputStream armored = new(publicKeyOut))
        {
            secretKey.PublicKey.Encode(armored);
        }

        return (Encoding.ASCII.GetString(publicKeyOut.ToArray()), secretKey);
    }

    private static string SignDetachedArmored(string data, PgpSecretKey secretKey)
    {
        PgpPrivateKey privateKey = secretKey.ExtractPrivateKey("test-passphrase".ToCharArray());
        PgpSignatureGenerator signatureGenerator = new(
            secretKey.PublicKey.Algorithm,
            HashAlgorithmTag.Sha256
        );
        signatureGenerator.InitSign(PgpSignature.BinaryDocument, privateKey);

        byte[] bytes = Encoding.UTF8.GetBytes(data);
        signatureGenerator.Update(bytes, 0, bytes.Length);
        PgpSignature signature = signatureGenerator.Generate();

        using MemoryStream signatureOut = new();
        using (ArmoredOutputStream armored = new(signatureOut))
        {
            signature.Encode(armored);
        }

        return Encoding.ASCII.GetString(signatureOut.ToArray());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private Binaries BuildBinaries(FakeHttpHandler handler)
    {
        LocalStorageDriver driver = new();
        StoragePathGuard guard = new([], driver);
        LocalStorage storage = new(driver, guard);
        HttpClient http = new(handler);
        return new(driver, storage, http);
    }

    private static GithubReleaseResponse BuildRelease(
        string assetName,
        string assetUrl,
        string? sha256,
        string? sha256Url = null,
        bool includeManifest = false,
        string? manifestUrl = null,
        string? manifestSigUrl = null,
        string? digest = null
    )
    {
        List<Asset> assets =
        [
            new()
            {
                Name = assetName,
                BrowserDownloadUrl = new(assetUrl),
                Size = 1,
                Digest = digest ?? string.Empty,
            },
        ];

        if (sha256 is not null && sha256Url is not null)
        {
            assets.Add(
                new()
                {
                    Name = assetName + ".sha256",
                    BrowserDownloadUrl = new(sha256Url),
                    Size = 64,
                }
            );
        }

        if (includeManifest && manifestUrl is not null)
        {
            assets.Add(
                new()
                {
                    Name = "manifest.json",
                    BrowserDownloadUrl = new(manifestUrl),
                    Size = 100,
                }
            );
        }

        if (manifestSigUrl is not null)
        {
            assets.Add(
                new()
                {
                    Name = "manifest.json.sig",
                    BrowserDownloadUrl = new(manifestSigUrl),
                    Size = 100,
                }
            );
        }

        return new()
        {
            TagName = "v1.0.0",
            PublishedAt = DateTimeOffset.UtcNow.AddDays(-1),
            Assets = [.. assets],
        };
    }
}

/// <summary>
/// Stub <see cref="HttpMessageHandler"/> that serves pre-registered byte arrays for
/// registered URLs and returns 404 for everything else.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, byte[]> _responses = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string url, byte[] body) => _responses[url] = body;

    public void RegisterRelease(GithubReleaseResponse release)
    {
        // Pre-register the GitHub API URL response as JSON
        // (not used by DownloadWithVerificationAsync directly — it receives the parsed object)
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        string url = request.RequestUri?.ToString() ?? string.Empty;
        if (_responses.TryGetValue(url, out byte[]? body))
        {
            HttpResponseMessage ok = new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };
            return Task.FromResult(ok);
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
