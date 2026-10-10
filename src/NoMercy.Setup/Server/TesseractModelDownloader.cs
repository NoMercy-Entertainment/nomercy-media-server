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

using NoMercy.Encoder.Subtitles;
using NoMercy.Setup.Dto;
using NoMercy.Storage;

namespace NoMercy.Setup.Server;

/// <summary>
/// Implements <see cref="ITesseractModelDownloader"/> against the same signed-release
/// machinery (<see cref="Binaries.GetLatestReleaseInfo"/>,
/// <see cref="Binaries.GetOrFetchManifestAsync"/>, <see cref="BinaryVerification"/>) that
/// the startup binary provisioning pipeline uses — so an on-demand OCR model pull is held
/// to the exact signature + SHA-256 bar as every other NoMercy-owned download.
/// </summary>
/// <remarks>
/// Deliberately stricter than <see cref="Binaries.DownloadWithVerificationAsync"/>'s
/// digest-fallback policy: this path is triggered on-demand for an asset the server has
/// never installed before (no "known-good binary already on disk" to fall back on), and
/// nomercy-tesseract's releases publish a signed manifest today, so a missing or
/// non-verifying signature is treated as a hard failure rather than a soft downgrade to
/// GitHub's asset digest. No unverified raw-URL fallback is ever attempted.
/// </remarks>
public class TesseractModelDownloader(
    IStorageDriver driver,
    IStorage storage,
    HttpClient httpClient
) : ITesseractModelDownloader
{
    private const string TesseractReleaseApiUrl =
        "https://api.github.com/repos/NoMercy-Entertainment/nomercy-tesseract/releases/latest";

    // Reuses Binaries' release-info cache (with its GitHub rate-limit backoff and
    // last-known-good disk cache) and its manifest cache — both keyed per apiUrl — so
    // repeated language downloads on this singleton never re-fetch or re-verify the same
    // release manifest.
    private readonly Binaries _binaries = new(driver, storage, httpClient);

    /// <summary>
    /// ISO 639-2 gave several languages two codes: a bibliographic one ("fre") and a
    /// terminological one ("fra"). Media files carry the bibliographic code, and asking
    /// the release for a spelling it does not publish fails as an unsigned asset rather
    /// than as a missing language — which is what turned a French subtitle into "No
    /// signed manifest entry for 'fre.traineddata'" on every OCR attempt.
    /// <para>
    /// The release is not consistent about which spelling it uses: it publishes
    /// <c>fra</c> for French but <c>ger</c> and <c>dut</c> for German and Dutch. So this
    /// is an alternate to fall back to, never a substitution — translating every code
    /// would break the languages that are already published under the bibliographic one.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> AlternateLanguageCodes = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["alb"] = "sqi",
        ["arm"] = "hye",
        ["baq"] = "eus",
        ["bur"] = "mya",
        ["chi"] = "chi_sim",
        ["cze"] = "ces",
        ["dut"] = "nld",
        ["fre"] = "fra",
        ["fra"] = "fre",
        ["deu"] = "ger",
        ["ger"] = "deu",
        ["nld"] = "dut",
        ["dut"] = "nld",
        ["geo"] = "kat",
        ["ger"] = "deu",
        ["gre"] = "ell",
        ["ice"] = "isl",
        ["mac"] = "mkd",
        ["mao"] = "mri",
        ["may"] = "msa",
        ["per"] = "fas",
        ["rum"] = "ron",
        ["slo"] = "slk",
        ["tib"] = "bod",
        ["wel"] = "cym",
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetAvailableLanguagesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        (GithubReleaseResponse releaseInfo, ReleaseManifest manifest) =
            await GetVerifiedReleaseAsync("language list");

        HashSet<string> releasedAssets = releaseInfo
            .Assets.Select(asset => asset.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. manifest
                .Assets.Where(asset =>
                    asset.Name.EndsWith(".traineddata", StringComparison.OrdinalIgnoreCase)
                    && releasedAssets.Contains(asset.Name)
                )
                .Select(asset => Path.GetFileNameWithoutExtension(asset.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
    }

    public async Task<Stream> DownloadVerifiedAsync(string language, CancellationToken ct)
    {
        string assetName = $"{language}.traineddata";
        (GithubReleaseResponse releaseInfo, ReleaseManifest manifest) =
            await GetVerifiedReleaseAsync(language);

        ct.ThrowIfCancellationRequested();

        ManifestAsset? manifestAsset = manifest.Assets.FirstOrDefault(a =>
            a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
        );

        if (
            manifestAsset is null
            && AlternateLanguageCodes.TryGetValue(language, out string? alternate)
        )
        {
            string alternateAsset = $"{alternate}.traineddata";
            manifestAsset = manifest.Assets.FirstOrDefault(a =>
                a.Name.Equals(alternateAsset, StringComparison.OrdinalIgnoreCase)
            );

            if (manifestAsset is not null)
                assetName = alternateAsset;
        }

        if (manifestAsset is null)
        {
            throw new InvalidOperationException(
                $"No signed manifest entry for '{assetName}' in the nomercy-tesseract release."
            );
        }

        Asset? releaseAsset = releaseInfo.Assets.FirstOrDefault(a =>
            a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
        );
        if (releaseAsset is null)
        {
            throw new InvalidOperationException(
                $"No release asset for '{assetName}' in the nomercy-tesseract release."
            );
        }

        using HttpResponseMessage response = await httpClient.GetAsync(
            releaseAsset.BrowserDownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );
        response.EnsureSuccessStatusCode();

        byte[] payload = await response.Content.ReadAsByteArrayAsync(ct);
        MemoryStream payloadStream = new(payload);

        bool hashOk = await BinaryVerification.VerifyStreamSha256Async(
            payloadStream,
            manifestAsset.Sha256,
            ct
        );
        if (!hashOk)
        {
            await payloadStream.DisposeAsync();
            throw new InvalidDataException(
                $"SHA-256 mismatch for '{assetName}': the downloaded model does not match the "
                    + "signed nomercy-tesseract manifest."
            );
        }

        payloadStream.Position = 0;
        return payloadStream;
    }

    private async Task<(
        GithubReleaseResponse ReleaseInfo,
        ReleaseManifest Manifest
    )> GetVerifiedReleaseAsync(string language)
    {
        GithubReleaseResponse releaseInfo = await _binaries.GetLatestReleaseInfo(
            TesseractReleaseApiUrl
        );
        if (releaseInfo.Assets.Length == 0)
        {
            throw new InvalidOperationException(
                $"Could not reach the nomercy-tesseract release — no signed model available for '{language}'."
            );
        }

        (ReleaseManifest? manifest, bool sigVerified, bool sigPresent) =
            await _binaries.GetOrFetchManifestAsync(TesseractReleaseApiUrl, releaseInfo);

        if (manifest is null)
        {
            throw new InvalidOperationException(
                $"No signed release manifest is available for nomercy-tesseract — refusing to "
                    + $"install an unverified model for '{language}'."
            );
        }

        if (!sigPresent || !sigVerified)
        {
            throw new InvalidOperationException(
                $"nomercy-tesseract release manifest signature could not be verified — refusing "
                    + $"to install an unverified model for '{language}'."
            );
        }

        return (releaseInfo, manifest);
    }
}
