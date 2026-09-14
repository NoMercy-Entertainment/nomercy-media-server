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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.Services;
using NoMercy.Data.Repositories;
using NoMercy.Data.Services;
using NoMercy.Database;
using NoMercy.Database.Activity;
using NoMercy.Database.Models.Media;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Profiles;
using Xunit;

namespace NoMercy.Tests.Api.Services;

/// <summary>
/// Guard-completeness suite for <see cref="EncoderProfileService.ImportAsync"/>.
///
/// Each import failure mode carries its OWN stable rule id — the whole point is
/// that a user who forgot the URL, sent bad JSON, or hit a dead link is told the
/// truth, not a generic "profile name missing" or "use HTTPS".
///
/// For every id:
///   (1) FIRES-ON-BAD — the failing input emits exactly that id.
///   (2) SILENT-ON-VALID-NEIGHBOR — the closest valid input does NOT emit it.
/// </summary>
[Trait("Category", "EncoderProfileImportGuard")]
public class EncoderProfileImportGuardTests
{
    private static readonly string ValidProfileJson =
        "{\"Id\":\""
        + Ulid.NewUlid()
        + "\",\"Name\":\"Imported\",\"Container\":\"HlsTs\",\"Video\":null,"
        + "\"Audio\":[{\"Policy\":\"Transcode\",\"Codec\":\"Aac\",\"BitrateKbps\":192,"
        + "\"Channels\":2,\"SampleRateHz\":48000,\"AllowedLanguages\":[],"
        + "\"DefaultLanguage\":null,\"Loudness\":null,\"Downmix\":null,"
        + "\"SegmentNameTemplate\":\"audio\",\"PlaylistNameTemplate\":\"audio\"}],"
        + "\"Subtitles\":[],\"SegmentDurationSeconds\":6}";

    private static MediaContext MakeContext()
    {
        SqliteConnection connection = new("DataSource=:memory:");
        connection.Open();
        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(connection)
            .Options;
        MediaContext context = new(options);
        context.Database.EnsureCreated();
        return context;
    }

    private static EncoderProfileService MakeService(HttpStatusCode? fetchStatus, string? fetchBody)
    {
        Mock<IEncodingPresetRepository> repository = new();
        repository
            .Setup(r => r.CreateAsync(It.IsAny<EncodingPreset>()))
            .ReturnsAsync((EncodingPreset preset) => preset);

        Mock<IActivityLogger> activityLogger = new();

        StubHttpMessageHandler handler = new(fetchStatus, fetchBody);
        Mock<IHttpClientFactory> httpClientFactory = new();
        httpClientFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new(handler));

        return new(
            repository.Object,
            activityLogger.Object,
            httpClientFactory.Object,
            MakeContext(),
            NullLogger<EncoderProfileService>.Instance
        );
    }

    private static Task<EncoderProfileService.ImportResult> ImportInline(string? inlineJson)
    {
        EncoderProfileService service = MakeService(null, null);
        return service.ImportAsync(
            inlineJson,
            url: null,
            trustUnsigned: true,
            signatureVerifier: new Mock<IProfileSignatureVerifier>().Object,
            userId: Guid.NewGuid(),
            ct: CancellationToken.None
        );
    }

    private static Task<EncoderProfileService.ImportResult> ImportFromUrl(
        string url,
        HttpStatusCode fetchStatus,
        string fetchBody
    )
    {
        EncoderProfileService service = MakeService(fetchStatus, fetchBody);
        return service.ImportAsync(
            inlineProfileJson: null,
            url: url,
            trustUnsigned: true,
            signatureVerifier: new Mock<IProfileSignatureVerifier>().Object,
            userId: Guid.NewGuid(),
            ct: CancellationToken.None
        );
    }

    private static bool EmitsError(EncoderProfileService.ImportResult result, string ruleId) =>
        result.ValidationError is not null
        && result.ValidationError.Errors.Any(e => e.Id == ruleId);

    // ---- import.source_missing ---------------------------------------------

    [Fact]
    public async Task Import_NeitherJsonNorUrl_EmitsSourceMissing()
    {
        EncoderProfileService service = MakeService(null, null);

        EncoderProfileService.ImportResult result = await service.ImportAsync(
            inlineProfileJson: null,
            url: null,
            trustUnsigned: true,
            signatureVerifier: new Mock<IProfileSignatureVerifier>().Object,
            userId: Guid.NewGuid(),
            ct: CancellationToken.None
        );

        EmitsError(result, EncoderRuleId.ImportSourceMissing).Should().BeTrue();
    }

    [Fact]
    public async Task Import_WithInlineJson_DoesNotEmitSourceMissing()
    {
        EncoderProfileService.ImportResult result = await ImportInline(ValidProfileJson);

        EmitsError(result, EncoderRuleId.ImportSourceMissing).Should().BeFalse();
    }

    // ---- import.json_malformed ---------------------------------------------

    [Fact]
    public async Task Import_MalformedInlineJson_EmitsJsonMalformed()
    {
        EncoderProfileService.ImportResult result = await ImportInline("{ this is not json");

        EmitsError(result, EncoderRuleId.ImportJsonMalformed).Should().BeTrue();
    }

    [Fact]
    public async Task Import_NullDeserializingJson_EmitsJsonMalformed()
    {
        EncoderProfileService.ImportResult result = await ImportInline("null");

        EmitsError(result, EncoderRuleId.ImportJsonMalformed).Should().BeTrue();
    }

    [Fact]
    public async Task Import_WellFormedJson_DoesNotEmitJsonMalformed()
    {
        EncoderProfileService.ImportResult result = await ImportInline(ValidProfileJson);

        EmitsError(result, EncoderRuleId.ImportJsonMalformed).Should().BeFalse();
    }

    // ---- import.http_not_https ---------------------------------------------

    [Fact]
    public async Task Import_PlainHttpUrl_EmitsHttpNotHttps()
    {
        EncoderProfileService service = MakeService(HttpStatusCode.OK, ValidProfileJson);

        EncoderProfileService.ImportResult result = await service.ImportAsync(
            inlineProfileJson: null,
            url: "http://example.com/profile.json",
            trustUnsigned: true,
            signatureVerifier: new Mock<IProfileSignatureVerifier>().Object,
            userId: Guid.NewGuid(),
            ct: CancellationToken.None
        );

        EmitsError(result, EncoderRuleId.ImportHttpNotHttps).Should().BeTrue();
    }

    [Fact]
    public async Task Import_HttpsUrl_DoesNotEmitHttpNotHttps()
    {
        EncoderProfileService.ImportResult result = await ImportFromUrl(
            "https://example.com/profile.json",
            HttpStatusCode.OK,
            ValidProfileJson
        );

        EmitsError(result, EncoderRuleId.ImportHttpNotHttps).Should().BeFalse();
    }

    // ---- import.fetch_failed -----------------------------------------------

    [Fact]
    public async Task Import_HttpsUrlReturns404_EmitsFetchFailed()
    {
        EncoderProfileService.ImportResult result = await ImportFromUrl(
            "https://example.com/missing.json",
            HttpStatusCode.NotFound,
            "not found"
        );

        EmitsError(result, EncoderRuleId.ImportFetchFailed).Should().BeTrue();
    }

    [Fact]
    public async Task Import_FetchFailed_IsNotReportedAsHttpNotHttps()
    {
        EncoderProfileService.ImportResult result = await ImportFromUrl(
            "https://example.com/missing.json",
            HttpStatusCode.NotFound,
            "not found"
        );

        EmitsError(result, EncoderRuleId.ImportHttpNotHttps).Should().BeFalse();
    }

    [Fact]
    public async Task Import_HttpsUrlReturns200_DoesNotEmitFetchFailed()
    {
        EncoderProfileService.ImportResult result = await ImportFromUrl(
            "https://example.com/profile.json",
            HttpStatusCode.OK,
            ValidProfileJson
        );

        EmitsError(result, EncoderRuleId.ImportFetchFailed).Should().BeFalse();
    }

    private sealed class StubHttpMessageHandler(HttpStatusCode? status, string? body)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            HttpResponseMessage response = new(status ?? HttpStatusCode.OK)
            {
                Content = new StringContent(body ?? string.Empty),
            };
            return Task.FromResult(response);
        }
    }
}
