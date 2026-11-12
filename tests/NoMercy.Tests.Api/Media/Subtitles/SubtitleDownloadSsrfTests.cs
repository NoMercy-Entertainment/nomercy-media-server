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
using System.Text;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Media.Subtitles;

/// <summary>
/// POST api/v1/subtitles/download fetches <c>download_url</c> server-side. That
/// field is fully client-controlled, so it must never be allowed to reach an
/// internal host (loopback / LAN / cloud link-local metadata) or a non-http(s)
/// scheme — the Jellyfin CVE-2026-35032 SSRF shape. These lock the guard that
/// rejects such URLs before any fetch.
/// </summary>
[Trait("Category", "Subtitles")]
public class SubtitleDownloadSsrfTests : IClassFixture<NoMercyApiFactory>
{
    private readonly HttpClient _authed;

    public SubtitleDownloadSsrfTests(NoMercyApiFactory factory)
    {
        _authed = factory.CreateClient().AsAuthenticated();
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")] // cloud metadata
    [InlineData("http://127.0.0.1:7626/api/v1/dashboard/server")] // loopback
    [InlineData("https://10.0.0.1/subs.srt")] // private LAN
    [InlineData("https://192.168.1.10/subs.srt")] // private LAN
    [InlineData("file:///etc/passwd")] // non-http scheme
    public async Task Download_RejectsServerSideRequestToInternalOrNonHttpUrl(string downloadUrl)
    {
        string payload =
            "{\"type\":\"movie\",\"id\":1,\"download_url\":\""
            + downloadUrl
            + "\",\"language\":\"en\"}";
        StringContent body = new(payload, Encoding.UTF8, "application/json");

        HttpResponseMessage response = await _authed.PostAsync("/api/v1/subtitles/download", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// The language goes into the sidecar file name and the stored Subtitles
    /// JSON, so only a language-code shape may pass.
    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("en us")]
    [InlineData("en;x")]
    [InlineData("a")]
    [InlineData("en\n")]
    public async Task Download_RejectsLanguageThatIsNotALanguageCode(string language)
    {
        string payload =
            "{\"type\":\"movie\",\"id\":1,\"download_url\":\"https://example.com/subs.srt\",\"language\":\""
            + language.Replace("\\", "\\\\").Replace("\n", "\\n")
            + "\"}";
        StringContent body = new(payload, Encoding.UTF8, "application/json");

        HttpResponseMessage response = await _authed.PostAsync("/api/v1/subtitles/download", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        string text = await response.Content.ReadAsStringAsync();
        text.Should().Contain("language");
    }
}
