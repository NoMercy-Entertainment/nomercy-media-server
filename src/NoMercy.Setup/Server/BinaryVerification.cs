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

using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Serilog.Events;

namespace NoMercy.Setup.Server;

/// <summary>
/// Represents a single asset entry inside a NoMercy release manifest.
/// </summary>
public sealed class ManifestAsset
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonProperty("size")]
    public long Size { get; set; }
}

/// <summary>
/// Represents the release manifest produced by the CI pipeline.
/// </summary>
public sealed class ReleaseManifest
{
    [JsonProperty("version")]
    public string Version { get; set; } = string.Empty;

    [JsonProperty("commit_sha")]
    public string CommitSha { get; set; } = string.Empty;

    [JsonProperty("build_timestamp")]
    public string BuildTimestamp { get; set; } = string.Empty;

    [JsonProperty("assets")]
    public ManifestAsset[] Assets { get; set; } = [];
}

/// <summary>
/// Provides SHA-256 file verification and PGP detached-signature verification for
/// release manifests.  Both operations are purely computational and have no I/O
/// side-effects beyond reading the file bytes.
/// </summary>
public static class BinaryVerification
{
    // Embedded org public key.  Replace the placeholder content with the real
    // ASCII-armored PGP public key exported from the org's signing key.
    private static readonly string? EmbeddedPublicKey = LoadEmbeddedPublicKey();

    /// <summary>
    /// Computes the SHA-256 hash of <paramref name="filePath"/> and compares it against
    /// <paramref name="expectedHex"/> (case-insensitive, hex-encoded).
    /// </summary>
    /// <returns>
    /// <c>true</c> when hashes match; <c>false</c> when they differ.
    /// </returns>
    public static async Task<bool> VerifyFileSha256Async(
        string filePath,
        string expectedHex,
        CancellationToken ct = default
    )
    {
        await using FileStream fs = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true
        );
        return await VerifyStreamSha256Async(fs, expectedHex, ct);
    }

    /// <summary>
    /// Computes the SHA-256 hash of <paramref name="stream"/> and compares it against
    /// <paramref name="expectedHex"/> (case-insensitive, hex-encoded). Shared core behind
    /// <see cref="VerifyFileSha256Async"/> so in-memory verification (e.g. a downloaded
    /// asset that has not been staged to disk yet) reuses the same comparison instead of
    /// recomputing SHA-256 inline at the call site.
    /// </summary>
    /// <returns>
    /// <c>true</c> when hashes match; <c>false</c> when they differ.
    /// </returns>
    public static async Task<bool> VerifyStreamSha256Async(
        Stream stream,
        string expectedHex,
        CancellationToken ct = default
    )
    {
        byte[] hash = await SHA256.HashDataAsync(stream, ct);
        string actual = Convert.ToHexString(hash);
        return string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the hex SHA-256 from a GitHub release-asset <c>digest</c> value of
    /// the form <c>sha256:&lt;hex&gt;</c>. GitHub computes this server-side for every
    /// asset, so it is a zero-cost integrity source already present in the release
    /// response.
    /// </summary>
    /// <returns>
    /// The bare hex digest, or <c>null</c> when the value is empty or uses an
    /// algorithm other than SHA-256.
    /// </returns>
    public static string? ExtractSha256FromDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
            return null;

        const string prefix = "sha256:";
        return digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? digest[prefix.Length..]
            : null;
    }

    /// <summary>
    /// Reads the SHA-256 out of a per-asset <c>.sha256</c> sidecar, in either form a
    /// release publishes it: the bare hex digest, or the line <c>sha256sum</c> prints,
    /// <c>"&lt;hex&gt; *&lt;filename&gt;"</c>.
    /// <para>
    /// The whole sidecar used to be taken as the digest. That holds for the bare form
    /// and fails for the other: the lyricsalign model's sidecar is a <c>sha256sum</c>
    /// line, and compared whole it could never match the file it describes, so the
    /// model would have been refused as tampered on every download.
    /// </para>
    /// </summary>
    /// <returns>
    /// The digest of <paramref name="assetName"/> when the sidecar names files; otherwise
    /// the sidecar's trimmed content, which a sidecar that is neither form then fails
    /// verification on rather than passing it.
    /// </returns>
    public static string ReadSha256Sidecar(string sidecarContent, string assetName) =>
        ParseSha256Sums(sidecarContent, assetName) ?? sidecarContent.Trim();

    /// <summary>
    /// Finds the SHA-256 for <paramref name="targetFileName"/> inside the content of a
    /// coreutils-style checksum file (e.g. yt-dlp's <c>SHA2-256SUMS</c>), where each
    /// line is <c>"&lt;hex&gt;  &lt;filename&gt;"</c>. A leading <c>'*'</c> binary marker
    /// on the filename is tolerated.
    /// </summary>
    /// <returns>The bare hex digest, or <c>null</c> when no line matches.</returns>
    public static string? ParseSha256Sums(string sumsContent, string targetFileName)
    {
        if (string.IsNullOrEmpty(sumsContent))
            return null;

        foreach (string line in sumsContent.Split('\n'))
        {
            string[] parts = line.Trim()
                .Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
                continue;

            string fileName = parts[1].TrimStart('*').Trim();
            if (fileName.Equals(targetFileName, StringComparison.OrdinalIgnoreCase))
                return parts[0].Trim();
        }

        return null;
    }

    /// <summary>
    /// Verifies a detached ASCII-armored PGP signature over <paramref name="manifestJson"/>
    /// using the org's embedded public key.
    /// </summary>
    /// <param name="manifestJson">The raw UTF-8 manifest text.</param>
    /// <param name="armoredSignature">The ASCII-armored detached signature (.sig file contents).</param>
    /// <returns>
    /// <c>true</c> on successful verification; <c>false</c> if the embedded key is
    /// unavailable or the signature does not match.
    /// </returns>
    public static bool VerifyManifestSignature(string manifestJson, string armoredSignature)
    {
        if (string.IsNullOrEmpty(EmbeddedPublicKey))
        {
            NmSystem.SystemCalls.Logger.Setup(
                "No embedded org public key — manifest signature not verified",
                LogEventLevel.Warning
            );
            return false;
        }

        return VerifyManifestSignature(manifestJson, armoredSignature, EmbeddedPublicKey);
    }

    /// <summary>
    /// Verifies a detached ASCII-armored PGP signature over <paramref name="manifestJson"/>
    /// using the supplied <paramref name="armoredPublicKey"/>.
    /// </summary>
    /// <remarks>
    /// This overload is exposed for testing with caller-supplied keys.
    /// </remarks>
    internal static bool VerifyManifestSignature(
        string manifestJson,
        string armoredSignature,
        string armoredPublicKey
    )
    {
        try
        {
            using MemoryStream keyStream = new(
                System.Text.Encoding.ASCII.GetBytes(armoredPublicKey)
            );
            using MemoryStream sigStream = new(
                System.Text.Encoding.ASCII.GetBytes(armoredSignature)
            );
            using MemoryStream dataStream = new(System.Text.Encoding.UTF8.GetBytes(manifestJson));

            using ArmoredInputStream armoredKey = new(keyStream);
            PgpPublicKeyRingBundle keyRingBundle = new(armoredKey);

            using ArmoredInputStream armoredSig = new(sigStream);
            PgpObjectFactory sigFactory = new(armoredSig);

            PgpSignatureList? signatureList = null;
            PgpObject? sigObj = sigFactory.NextPgpObject();
            while (sigObj is not null)
            {
                if (sigObj is PgpSignatureList sl)
                {
                    signatureList = sl;
                    break;
                }

                sigObj = sigFactory.NextPgpObject();
            }

            if (signatureList is null || signatureList.Count == 0)
                return false;

            for (int i = 0; i < signatureList.Count; i++)
            {
                PgpSignature signature = signatureList[i];
                PgpPublicKey? pubKey = keyRingBundle.GetPublicKey(signature.KeyId);
                if (pubKey is null)
                    continue;

                signature.InitVerify(pubKey);

                byte[] data = System.Text.Encoding.UTF8.GetBytes(manifestJson);
                signature.Update(data, 0, data.Length);

                if (signature.Verify())
                    return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            NmSystem.SystemCalls.Logger.Setup(
                $"Manifest signature verification error: {ex.Message}",
                LogEventLevel.Warning
            );
            return false;
        }
    }

    private static string? LoadEmbeddedPublicKey()
    {
        try
        {
            Assembly assembly = typeof(BinaryVerification).Assembly;
            string resourceName = "NoMercy.Setup.Resources.nomercy-public-key.asc";
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
                return null;

            using StreamReader reader = new(stream);
            string content = reader.ReadToEnd().Trim();

            // Treat a placeholder/empty file as "not configured"
            if (
                string.IsNullOrWhiteSpace(content)
                || content.StartsWith("# PLACEHOLDER", StringComparison.OrdinalIgnoreCase)
                || !content.Contains("BEGIN PGP PUBLIC KEY BLOCK", StringComparison.Ordinal)
            )
                return null;

            return content;
        }
        catch
        {
            return null;
        }
    }
}
