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

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Moq;
using Newtonsoft.Json.Linq;
using NoMercy.Api.Controllers.V1.Encoder;
using NoMercy.Data.Repositories;
using NoMercy.Data.Services;
using NoMercy.Database;
using NoMercy.Database.Activity;
using NoMercy.Database.Models.Media;
using NoMercy.Encoder.Profiles;
using Xunit;

namespace NoMercy.Tests.Api.Controllers;

/// <summary>
/// Drives <see cref="EncoderProfilesController"/> directly against a real
/// <see cref="EncodingPresetRepository"/> over an isolated in-memory SQLite
/// database, so each test sees exactly the presets it created.
/// </summary>
[Trait("Category", "Unit")]
public class EncoderProfilesControllerTests
{
    private static readonly Guid ModeratorId = Guid.NewGuid();

    private sealed class Harness : IDisposable
    {
        private readonly SqliteConnection _connection;

        public MediaContext Context { get; }
        public EncodingPresetRepository Repository { get; }
        public EncoderProfilesController Controller { get; }

        /// <param name="remoteProfileJson">
        /// When set, every HTTPS fetch the import performs answers 200 with this body.
        /// </param>
        public Harness(string? remoteProfileJson = null)
        {
            _connection = new("DataSource=:memory:");
            _connection.Open();

            DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
                .UseSqlite(_connection)
                .Options;
            Context = new(options);
            Context.Database.EnsureCreated();

            Repository = new(Context);

            Mock<IHttpClientFactory> httpClientFactory = new();
            httpClientFactory
                .Setup(factory => factory.CreateClient(It.IsAny<string>()))
                .Returns(() => new(new StubHttpMessageHandler(remoteProfileJson ?? string.Empty)));

            EncoderProfileService service = new(
                Repository,
                Mock.Of<IActivityLogger>(),
                httpClientFactory.Object,
                Context,
                NullLogger<EncoderProfileService>.Instance
            );

            DefaultHttpContext httpContext = new()
            {
                User = new(
                    new ClaimsIdentity(
                        [new(ClaimTypes.NameIdentifier, ModeratorId.ToString())],
                        "test"
                    )
                ),
            };

            Controller = new(
                Mock.Of<IProfileValidator>(),
                Mock.Of<IProfileSignatureVerifier>(),
                Context,
                Repository,
                service
            )
            {
                ControllerContext = new() { HttpContext = httpContext },
            };
        }

        public void Dispose()
        {
            Context.Dispose();
            _connection.Dispose();
        }
    }

    private sealed class StubHttpMessageHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(body),
                }
            );
    }

    private static readonly string ValidProfileJson =
        "{\"Id\":\""
        + Ulid.NewUlid()
        + "\",\"Name\":\"Imported\",\"Container\":\"HlsTs\",\"Video\":null,"
        + "\"Audio\":[{\"Policy\":\"Transcode\",\"Codec\":\"Aac\",\"BitrateKbps\":192,"
        + "\"Channels\":2,\"SampleRateHz\":48000,\"AllowedLanguages\":[],"
        + "\"DefaultLanguage\":null,\"Loudness\":null,\"Downmix\":null,"
        + "\"SegmentNameTemplate\":\"audio\",\"PlaylistNameTemplate\":\"audio\"}],"
        + "\"Subtitles\":[],\"SegmentDurationSeconds\":6}";

    private static EncodingPreset Preset(string name, string? tags = null) =>
        new()
        {
            Name = name,
            Tags = tags,
            ProfileJson = "{}",
            IsBuiltIn = false,
        };

    private static JObject BodyOf(IActionResult response) =>
        JObject.FromObject(response.Should().BeOfType<OkObjectResult>().Subject.Value!);

    // -------------------------------------------------------------------------
    // Index
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Index_WithTagFilter_ReportsTheFilteredTotal()
    {
        using Harness harness = new();
        await harness.Repository.CreateAsync(Preset("Anime", tags: "x"));
        await harness.Repository.CreateAsync(Preset("Archive"));
        await harness.Repository.CreateAsync(Preset("Mobile"));

        IActionResult response = await harness.Controller.Index(
            pageSize: 100,
            pageIndex: 0,
            tag: "x"
        );

        JObject body = BodyOf(response);
        body["data"]!.Should().HaveCount(1);
        ((int)body["meta"]!["total"]!)
            .Should()
            .Be(1, "meta.total must count only the profiles that match the tag filter");
        ((int)body["meta"]!["totalPages"]!)
            .Should()
            .Be(1, "meta.totalPages must be derived from the filtered total");
    }

    [Fact]
    public async Task Index_WithoutTagFilter_StillReportsEveryProfile()
    {
        using Harness harness = new();
        await harness.Repository.CreateAsync(Preset("Anime", tags: "x"));
        await harness.Repository.CreateAsync(Preset("Archive"));
        await harness.Repository.CreateAsync(Preset("Mobile"));

        IActionResult response = await harness.Controller.Index(pageSize: 2, pageIndex: 0);

        JObject body = BodyOf(response);
        body["data"]!.Should().HaveCount(2);
        ((int)body["meta"]!["total"]!).Should().Be(3);
        ((int)body["meta"]!["totalPages"]!).Should().Be(2);
    }

    // -------------------------------------------------------------------------
    // Export
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Export_PresetNameWithQuotesSlashesAndUnicode_DownloadsWithASafeFilename()
    {
        using Harness harness = new();
        EncodingPreset preset = await harness.Repository.CreateAsync(
            Preset("He said \"hi\" / ünïcode")
        );

        IActionResult response = await harness.Controller.Export(preset.Id.ToString());

        ContentResult content = response.Should().BeOfType<ContentResult>().Subject;
        (content.StatusCode ?? StatusCodes.Status200OK).Should().Be(StatusCodes.Status200OK);
        content.Content.Should().Be(preset.ProfileJson);

        string fileName = ExportedFileName(harness.Controller);
        fileName.Should().EndWith(".json");
        fileName.Should().NotContainAny("\"", "/", "\\");
    }

    [Fact]
    public async Task Export_PresetNameWithOnlyUnsafeCharacters_FallsBackToThePresetId()
    {
        using Harness harness = new();
        EncodingPreset preset = await harness.Repository.CreateAsync(Preset("\"/\\\""));

        IActionResult response = await harness.Controller.Export(preset.Id.ToString());

        response.Should().BeOfType<ContentResult>();
        ExportedFileName(harness.Controller).Should().Be($"{preset.Id}.json");
    }

    // -------------------------------------------------------------------------
    // Import
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Import_SameProfileTwice_RefusesTheDuplicateNameAndKeepsOnePreset()
    {
        using Harness harness = new();
        ImportProfileRequest request = new(ValidProfileJson, Url: null);

        IActionResult first = await harness.Controller.Import(request, trust_unsigned: true);
        IActionResult second = await harness.Controller.Import(request, trust_unsigned: true);

        first.Should().BeOfType<CreatedAtActionResult>();
        ObjectResult conflict = second.Should().BeAssignableTo<ObjectResult>().Subject;
        conflict
            .StatusCode.Should()
            .Be(
                StatusCodes.Status409Conflict,
                "importing a profile whose name already exists must be refused like create refuses it"
            );
        conflict
            .Value.Should()
            .BeOfType<ProblemDetails>()
            .Which.Detail.Should()
            .Contain("Imported");
        (await harness.Repository.GetTotalCountAsync())
            .Should()
            .Be(1, "the duplicate import must not store a second preset");
    }

    [Fact]
    public async Task Import_FromUrl_RefusesANameThatAlreadyExists()
    {
        using Harness harness = new(remoteProfileJson: ValidProfileJson);
        await harness.Repository.CreateAsync(Preset("Imported"));

        IActionResult response = await harness.Controller.Import(
            new(ProfileJson: null, Url: "https://example.com/profile.json"),
            trust_unsigned: true
        );

        response
            .Should()
            .BeAssignableTo<ObjectResult>()
            .Which.StatusCode.Should()
            .Be(StatusCodes.Status409Conflict, "a URL import follows the same unique-name rule");
        (await harness.Repository.GetTotalCountAsync()).Should().Be(1);
    }

    private static string ExportedFileName(EncoderProfilesController controller)
    {
        string header = controller.Response.Headers.ContentDisposition.ToString();

        ContentDispositionHeaderValue
            .TryParse(header, out ContentDispositionHeaderValue? disposition)
            .Should()
            .BeTrue($"the Content-Disposition header must be well-formed, but was: {header}");
        disposition!.DispositionType.ToString().Should().Be("attachment");

        StringSegment fileName = disposition.FileNameStar.HasValue
            ? disposition.FileNameStar
            : disposition.FileName;
        fileName.HasValue.Should().BeTrue("the download must carry a filename");

        return fileName.ToString().Trim('"');
    }
}
