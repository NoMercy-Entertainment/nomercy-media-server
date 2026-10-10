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
using NoMercy.NmSystem.Information;
using NoMercy.Setup.Dto;
using NoMercy.Setup.Server;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Setup;

/// <summary>
/// Tests <see cref="TesseractModelDownloader"/>'s hard-fail policy: unlike
/// <see cref="Binaries.DownloadWithVerificationAsync"/>'s opportunistic digest fallback
/// (appropriate for the general provisioning pipeline where a known-good binary is
/// already on disk), an on-demand OCR model pull has no unverified fallback to fall
/// back to — a manifest that does not verify, or is missing, must hard-fail closed.
/// </summary>
/// <remarks>
/// The org's real embedded public key is compiled into NoMercy.Setup (not a test
/// placeholder), so a genuinely-verifying signature cannot be forged here without its
/// private counterpart — the same constraint the existing
/// <c>BinaryDownloaderTests</c> "owned repo" cases already live with. These tests cover
/// every branch reachable without that key: signature absent, signature present but
/// invalid, and the release being unreachable. The success path (signature verifies +
/// hash matches → model written) is covered end-to-end at the
/// <c>TesseractModelManagerTests</c> layer against a fake
/// <see cref="NoMercy.Encoder.Subtitles.ITesseractModelDownloader"/>.
/// </remarks>
[Trait("Category", "Unit")]
public class TesseractModelDownloaderTests
{
    private const string TesseractApiUrl =
        "https://api.github.com/repos/NoMercy-Entertainment/nomercy-tesseract/releases/latest";

    public TesseractModelDownloaderTests()
    {
        // Binaries.GetLatestReleaseInfo persists a last-known-good cache to disk keyed by
        // apiUrl (survives across test runs and process instances by design — see
        // ReleaseCacheFallbackTests). TesseractReleaseApiUrl is a fixed production
        // constant every test in this file shares, so a cache entry written by one test
        // (or a prior run) would otherwise leak into the next. Clear it before every test.
        ClearCachedReleaseInfo(TesseractApiUrl);
    }

    private static void ClearCachedReleaseInfo(string apiUrl)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiUrl));
        string fileName = Convert.ToHexString(hash).ToLowerInvariant() + ".json";
        string path = Path.Combine(AppFiles.CachePath, "releases-cache", fileName);
        if (File.Exists(path))
            File.Delete(path);
    }

    [Fact]
    public async Task GetAvailableLanguagesAsync_UnsignedManifest_Throws()
    {
        string manifestUrl = "https://example.com/manifest.json";
        ReleaseManifest manifest = new()
        {
            Version = "1.0.1",
            Assets =
            [
                new()
                {
                    Name = "eng.traineddata",
                    Sha256 = new('a', 64),
                    Size = 10,
                },
                new()
                {
                    Name = "jpn.traineddata",
                    Sha256 = new('b', 64),
                    Size = 10,
                },
            ],
        };
        GithubReleaseResponse release = BuildRelease(
            assetUrl: "https://example.com/eng.traineddata",
            manifestUrl: manifestUrl,
            manifestSigUrl: null,
            extraAssetName: "jpn.traineddata",
            extraAssetUrl: "https://example.com/jpn.traineddata"
        );
        CountingFakeHandler handler = new();
        handler.Register(TesseractApiUrl, JsonBytes(release));
        handler.Register(manifestUrl, JsonBytes(manifest));

        TesseractModelDownloader downloader = BuildDownloader(handler);

        Func<Task> act = () => downloader.GetAvailableLanguagesAsync(CancellationToken.None);
        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*signature could not be verified*");
    }

    [Fact]
    public async Task DownloadVerifiedAsync_ReleaseUnreachable_Throws()
    {
        CountingFakeHandler handler = new();
        // No release JSON registered at all -> FromJson returns an empty response with
        // Assets.Length == 0, mirroring GetLatestReleaseInfo's own empty-fallback path.
        TesseractModelDownloader downloader = BuildDownloader(handler);

        Func<Task> act = () => downloader.DownloadVerifiedAsync("eng", CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*Could not reach the nomercy-tesseract release*");
    }

    [Fact]
    public async Task DownloadVerifiedAsync_NoManifestPublished_Throws()
    {
        // Release exists but carries no manifest.json asset at all.
        GithubReleaseResponse release = BuildRelease(
            assetUrl: "https://example.com/eng.traineddata",
            manifestUrl: null,
            manifestSigUrl: null
        );

        CountingFakeHandler handler = new();
        handler.Register(TesseractApiUrl, JsonBytes(release));

        TesseractModelDownloader downloader = BuildDownloader(handler);

        Func<Task> act = () => downloader.DownloadVerifiedAsync("eng", CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*No signed release manifest is available*");
    }

    [Fact]
    public async Task DownloadVerifiedAsync_ManifestPresentNoSignatureAsset_Throws()
    {
        // A manifest exists but no manifest.json.sig was ever published for this release.
        // Binaries' general pipeline treats this as "rollout in progress" and falls back
        // to the GitHub digest; the on-demand tesseract path has no such fallback and
        // must reject instead of installing an unverified model.
        string manifestUrl = "https://example.com/manifest.json";
        ReleaseManifest manifest = new()
        {
            Version = "1.0.1",
            Assets =
            [
                new()
                {
                    Name = "eng.traineddata",
                    Sha256 = new('a', 64),
                    Size = 10,
                },
            ],
        };

        GithubReleaseResponse release = BuildRelease(
            assetUrl: "https://example.com/eng.traineddata",
            manifestUrl: manifestUrl,
            manifestSigUrl: null
        );

        CountingFakeHandler handler = new();
        handler.Register(TesseractApiUrl, JsonBytes(release));
        handler.Register(manifestUrl, JsonBytes(manifest));

        TesseractModelDownloader downloader = BuildDownloader(handler);

        Func<Task> act = () => downloader.DownloadVerifiedAsync("eng", CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*signature could not be verified*");
    }

    [Fact]
    public async Task DownloadVerifiedAsync_ManifestSignatureDoesNotVerify_Throws()
    {
        // A .sig asset is published but does not verify against the embedded key (stale
        // key, forged manifest, CI signing glitch) — a real tamper/rotation signal that
        // must hard-fail, never silently fall back to an unverified download.
        string manifestUrl = "https://example.com/manifest.json";
        string sigUrl = "https://example.com/manifest.json.sig";
        ReleaseManifest manifest = new()
        {
            Version = "1.0.1",
            Assets =
            [
                new()
                {
                    Name = "eng.traineddata",
                    Sha256 = new('a', 64),
                    Size = 10,
                },
            ],
        };

        GithubReleaseResponse release = BuildRelease(
            assetUrl: "https://example.com/eng.traineddata",
            manifestUrl: manifestUrl,
            manifestSigUrl: sigUrl
        );

        CountingFakeHandler handler = new();
        handler.Register(TesseractApiUrl, JsonBytes(release));
        handler.Register(manifestUrl, JsonBytes(manifest));
        handler.Register(
            sigUrl,
            System.Text.Encoding.ASCII.GetBytes(
                "-----BEGIN PGP SIGNATURE-----\nnot-a-real-signature\n-----END PGP SIGNATURE-----"
            )
        );

        TesseractModelDownloader downloader = BuildDownloader(handler);

        Func<Task> act = () => downloader.DownloadVerifiedAsync("eng", CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*signature could not be verified*");
    }

    [Fact]
    public async Task DownloadVerifiedAsync_RepeatedCalls_ReuseCachedManifest()
    {
        // Binaries.GetOrFetchManifestAsync caches per apiUrl in-memory, so the manifest
        // fetch + signature verification — the expensive, security-sensitive step this
        // requirement is about — happens once per process lifetime (the
        // TesseractModelDownloader is a DI singleton), not once per language. The release
        // metadata GET itself is cheap and, by Binaries' existing design, is not
        // in-memory-cached (only ever served from the on-disk fallback when the live
        // fetch fails) — so it is expected to run once per call here.
        string manifestUrl = "https://example.com/manifest.json";
        string sigUrl = "https://example.com/manifest.json.sig";
        ReleaseManifest manifest = new()
        {
            Version = "1.0.1",
            Assets =
            [
                new()
                {
                    Name = "eng.traineddata",
                    Sha256 = new('a', 64),
                    Size = 10,
                },
                new()
                {
                    Name = "jpn.traineddata",
                    Sha256 = new('b', 64),
                    Size = 10,
                },
            ],
        };

        GithubReleaseResponse release = BuildRelease(
            assetUrl: "https://example.com/eng.traineddata",
            manifestUrl: manifestUrl,
            manifestSigUrl: sigUrl,
            extraAssetName: "jpn.traineddata",
            extraAssetUrl: "https://example.com/jpn.traineddata"
        );

        CountingFakeHandler handler = new();
        handler.Register(TesseractApiUrl, JsonBytes(release));
        handler.Register(manifestUrl, JsonBytes(manifest));
        handler.Register(
            sigUrl,
            System.Text.Encoding.ASCII.GetBytes(
                "-----BEGIN PGP SIGNATURE-----\nnot-a-real-signature\n-----END PGP SIGNATURE-----"
            )
        );

        TesseractModelDownloader downloader = BuildDownloader(handler);

        // Both calls fail (the signature never verifies against the real embedded key in
        // this test environment) — what matters here is how many times the release and
        // manifest endpoints were actually hit.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            downloader.DownloadVerifiedAsync("eng", CancellationToken.None)
        );
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            downloader.DownloadVerifiedAsync("jpn", CancellationToken.None)
        );

        // The release metadata GET runs once per call (Binaries does not in-memory-cache
        // it); the manifest + signature verification is the part that must not repeat.
        handler.CallCount(TesseractApiUrl).Should().Be(2);
        handler.CallCount(manifestUrl).Should().Be(1);
        handler.CallCount(sigUrl).Should().Be(1);
    }

    private static TesseractModelDownloader BuildDownloader(HttpMessageHandler handler)
    {
        LocalStorageDriver driver = new();
        LocalStorage storage = new(driver, new([], driver));
        return new(driver, storage, new(handler));
    }

    private static byte[] JsonBytes(object value) =>
        System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value));

    private static GithubReleaseResponse BuildRelease(
        string assetUrl,
        string? manifestUrl,
        string? manifestSigUrl,
        string? extraAssetName = null,
        string? extraAssetUrl = null
    )
    {
        List<Asset> assets =
        [
            new()
            {
                Name = "eng.traineddata",
                BrowserDownloadUrl = new(assetUrl),
                Size = 10,
            },
        ];

        if (extraAssetName is not null && extraAssetUrl is not null)
        {
            assets.Add(
                new()
                {
                    Name = extraAssetName,
                    BrowserDownloadUrl = new(extraAssetUrl),
                    Size = 10,
                }
            );
        }

        if (manifestUrl is not null)
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
            TagName = "v1.0.1",
            PublishedAt = DateTimeOffset.UtcNow.AddDays(-1),
            Assets = [.. assets],
        };
    }

    /// <summary>
    /// Stub handler that serves pre-registered byte arrays and counts requests per URL —
    /// used to prove the manifest cache is not bypassed across repeated language
    /// downloads. Kept local to this file rather than sharing
    /// <c>BinaryDownloaderTests.FakeHttpHandler</c> so this slice does not modify test
    /// infrastructure another change may be relying on.
    /// </summary>
    private sealed class CountingFakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _responses = new(
            StringComparer.OrdinalIgnoreCase
        );
        private readonly Dictionary<string, int> _callCounts = new(
            StringComparer.OrdinalIgnoreCase
        );

        public void Register(string url, byte[] body) => _responses[url] = body;

        public int CallCount(string url) => _callCounts.GetValueOrDefault(url);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string url = request.RequestUri?.ToString() ?? string.Empty;
            _callCounts[url] = _callCounts.GetValueOrDefault(url) + 1;

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
}
