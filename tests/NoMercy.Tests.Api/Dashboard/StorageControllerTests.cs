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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Newtonsoft.Json.Linq;
using NoMercy.Api.Services;
using NoMercy.Service;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// The storage endpoints through the real pipeline, with the services behind
/// them replaced, so no test reads or cleans a real folder.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StorageControllerTests(NoMercyApiFactory factory)
    : IClassFixture<NoMercyApiFactory>
{
    private const string Route = "/api/v1/dashboard/storage";

    private readonly Mock<IStorageReportService> _report = new();
    private readonly Mock<IStorageCleanupService> _cleanup = new();
    private readonly Mock<IStorageSettingsService> _settings = new();

    private WebApplicationFactory<Startup> Host() =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IStorageReportService>();
                services.RemoveAll<IStorageCleanupService>();
                services.RemoveAll<IStorageSettingsService>();
                services.AddSingleton(_report.Object);
                services.AddSingleton(_cleanup.Object);
                services.AddSingleton(_settings.Object);
            })
        );

    private static StorageSettings DefaultSettings() => new(3, 30, 3, 10240, 14, 4);

    [Theory]
    [InlineData("GET", Route)]
    [InlineData("POST", Route + "/cleanup")]
    [InlineData("GET", Route + "/settings")]
    [InlineData("PATCH", Route + "/settings")]
    public async Task ASignedInNonOwner_IsRefused_AndNothingHappens(string method, string url)
    {
        using WebApplicationFactory<Startup> host = Host();
        using HttpClient member = host.CreateClient().AsSecondaryUser();
        using HttpRequestMessage request = new(new(method), url);
        if (method == "PATCH")
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await member.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _report.Invocations.Should().BeEmpty();
        _cleanup.Invocations.Should().BeEmpty();
        _settings.Invocations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("GET", Route)]
    [InlineData("POST", Route + "/cleanup")]
    public async Task AnAnonymousCaller_IsRefused(string method, string url)
    {
        using WebApplicationFactory<Startup> host = Host();
        using HttpClient anonymous = host.CreateClient().AsUnauthenticated();

        using HttpResponseMessage response = await anonymous.SendAsync(new(new(method), url));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _cleanup.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Report_ListsEveryPartWithItsBytes_AndNoPath()
    {
        _report
            .Setup(service => service.ReadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new StorageReport(
                    [
                        new("database_media", "databases", 1000, 400),
                        new("backup_sets", "backups", 5000, 0),
                    ],
                    123456
                )
            );
        using WebApplicationFactory<Startup> host = Host();
        using HttpClient owner = host.CreateClient().AsAuthenticated();

        using HttpResponseMessage response = await owner.GetAsync(Route);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string text = await response.Content.ReadAsStringAsync();
        JObject body = JObject.Parse(text);
        body["total_bytes"]!.Value<long>().Should().Be(6000);
        body["free_disk_bytes"]!.Value<long>().Should().Be(123456);
        body["parts"]![0]!["id"]!.Value<string>().Should().Be("database_media");
        body["parts"]![0]!["group"]!.Value<string>().Should().Be("databases");
        body["parts"]![0]!["bytes"]!.Value<long>().Should().Be(1000);
        body["parts"]![0]!["reclaimable_bytes"]!.Value<long>().Should().Be(400);
        text.Should().NotContain("/").And.NotContain("\\\\");
    }

    [Fact]
    public async Task Cleanup_ReportsTheBytesFreedPerPart()
    {
        _cleanup
            .Setup(service => service.RunAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new StorageCleanupResult([new("database_media", 700), new("image_cache", 300)], 12)
            );
        using WebApplicationFactory<Startup> host = Host();
        using HttpClient owner = host.CreateClient().AsAuthenticated();

        using HttpResponseMessage response = await owner.PostAsync(Route + "/cleanup", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JObject body = JObject.Parse(await response.Content.ReadAsStringAsync());
        body["total_bytes_freed"]!.Value<long>().Should().Be(1000);
        body["failed_jobs_removed"]!.Value<int>().Should().Be(12);
        body["parts"]![1]!["id"]!.Value<string>().Should().Be("image_cache");
        body["parts"]![1]!["bytes_freed"]!.Value<long>().Should().Be(300);
    }

    [Fact]
    public async Task Cleanup_WhileAnotherPassRuns_AnswersConflict()
    {
        _cleanup
            .Setup(service => service.RunAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((StorageCleanupResult?)null);
        using WebApplicationFactory<Startup> host = Host();
        using HttpClient owner = host.CreateClient().AsAuthenticated();

        using HttpResponseMessage response = await owner.PostAsync(Route + "/cleanup", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Settings_ShowsTheCurrentValues()
    {
        _settings
            .Setup(service => service.Read())
            .Returns(new StorageSettings(2, 7, 5, 2048, 21, 6));
        using WebApplicationFactory<Startup> host = Host();
        using HttpClient owner = host.CreateClient().AsAuthenticated();

        using HttpResponseMessage response = await owner.GetAsync(Route + "/settings");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JObject body = JObject.Parse(await response.Content.ReadAsStringAsync());
        body["database_hour"]!.Value<int>().Should().Be(2);
        body["failed_job_retention_days"]!.Value<int>().Should().Be(7);
        body["backup_retain_count"]!.Value<int>().Should().Be(5);
        body["image_cache_max_megabytes"]!.Value<int>().Should().Be(2048);
        body["image_cache_orphan_grace_days"]!.Value<int>().Should().Be(21);
        body["image_cache_hour"]!.Value<int>().Should().Be(6);
    }

    [Fact]
    public async Task UpdateSettings_PassesOnlyTheFieldsGiven_AndSaysWhenARestartIsNeeded()
    {
        StorageSettingsPatch? received = null;
        _settings
            .Setup(service =>
                service.ApplyAsync(It.IsAny<StorageSettingsPatch>(), It.IsAny<Guid>())
            )
            .Callback<StorageSettingsPatch, Guid>((patch, _) => received = patch)
            .ReturnsAsync(new StorageSettingsChange(DefaultSettings(), null, true));
        using WebApplicationFactory<Startup> host = Host();
        using HttpClient owner = host.CreateClient().AsAuthenticated();
        using StringContent body = new(
            "{\"image_cache_max_megabytes\": 2048}",
            Encoding.UTF8,
            "application/json"
        );

        using HttpResponseMessage response = await owner.PatchAsync(Route + "/settings", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        received.Should().NotBeNull();
        received!.ImageCacheMaxMegabytes.Should().Be(2048);
        received.DatabaseHour.Should().BeNull();
        received.BackupRetainCount.Should().BeNull();
        JObject answer = JObject.Parse(await response.Content.ReadAsStringAsync());
        answer["restart_required"]!.Value<bool>().Should().BeTrue();
        answer["settings"]!["image_cache_hour"]!.Value<int>().Should().Be(4);
    }

    [Fact]
    public async Task UpdateSettings_WithAValueOutOfRange_AnswersBadRequest()
    {
        _settings
            .Setup(service =>
                service.ApplyAsync(It.IsAny<StorageSettingsPatch>(), It.IsAny<Guid>())
            )
            .ReturnsAsync(new StorageSettingsChange(null, "database_hour must be 0 to 23.", false));
        using WebApplicationFactory<Startup> host = Host();
        using HttpClient owner = host.CreateClient().AsAuthenticated();
        using StringContent body = new(
            "{\"database_hour\": 99}",
            Encoding.UTF8,
            "application/json"
        );

        using HttpResponseMessage response = await owner.PatchAsync(Route + "/settings", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
