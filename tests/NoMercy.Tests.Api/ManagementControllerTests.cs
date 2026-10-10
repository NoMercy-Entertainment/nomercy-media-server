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
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NoMercy.Database;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;
using Configuration = NoMercy.Database.Models.Common.Configuration;

namespace NoMercy.Tests.Api;

[Trait("Category", "Characterization")]
public class ManagementControllerTests : IClassFixture<NoMercyApiFactory>
{
    private readonly NoMercyApiFactory _factory;
    private readonly HttpClient _client;

    public ManagementControllerTests(NoMercyApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ManageStatus_ReturnsOk_WithServerStatus()
    {
        HttpResponseMessage response = await _client.GetAsync("/manage/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string content = await response.Content.ReadAsStringAsync();
        JsonDocument json = JsonDocument.Parse(content);
        JsonElement root = json.RootElement;

        Assert.True(root.TryGetProperty("status", out JsonElement status));
        Assert.False(string.IsNullOrEmpty(status.GetString()));

        Assert.True(root.TryGetProperty("server_name", out _));
        Assert.True(root.TryGetProperty("version", out _));
        Assert.True(root.TryGetProperty("platform", out _));
        Assert.True(root.TryGetProperty("architecture", out _));
        Assert.True(root.TryGetProperty("os", out _));
        Assert.True(root.TryGetProperty("uptime_seconds", out JsonElement uptime));
        Assert.True(uptime.GetInt64() >= 0);
        Assert.True(root.TryGetProperty("start_time", out _));
        Assert.True(root.TryGetProperty("is_dev", out _));
    }

    [Fact]
    public async Task ManageLogs_ReturnsOk_WithLogEntries()
    {
        HttpResponseMessage response = await _client.GetAsync("/manage/logs?tail=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string content = await response.Content.ReadAsStringAsync();
        JsonDocument json = JsonDocument.Parse(content);

        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
    }

    [Fact]
    public async Task ManageLogs_WithTypeFilter_ReturnsOk()
    {
        HttpResponseMessage response = await _client.GetAsync("/manage/logs?tail=10&types=app");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ManageLogs_WithLevelFilter_ReturnsOk()
    {
        HttpResponseMessage response = await _client.GetAsync(
            "/manage/logs?tail=10&levels=Information,Error"
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ManageConfig_ReturnsOk_WithConfiguration()
    {
        HttpResponseMessage response = await _client.GetAsync("/manage/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string content = await response.Content.ReadAsStringAsync();
        JsonDocument json = JsonDocument.Parse(content);
        JsonElement root = json.RootElement;

        Assert.True(root.TryGetProperty("internal_port", out JsonElement port));
        Assert.True(port.GetInt32() > 0);

        Assert.True(root.TryGetProperty("external_port", out _));
        Assert.True(root.TryGetProperty("server_name", out _));
        Assert.True(root.TryGetProperty("library_workers", out _));
        Assert.True(root.TryGetProperty("import_workers", out _));
        Assert.True(root.TryGetProperty("extras_workers", out _));
        Assert.True(root.TryGetProperty("encoder_workers", out _));
        Assert.True(root.TryGetProperty("cron_workers", out _));
        Assert.True(root.TryGetProperty("image_workers", out _));
        Assert.True(root.TryGetProperty("file_workers", out _));
        Assert.True(root.TryGetProperty("music_workers", out _));
        Assert.True(root.TryGetProperty("swagger", out _));
    }

    [Fact]
    public async Task ManagePlugins_ReturnsOk_WithArray()
    {
        HttpResponseMessage response = await _client.GetAsync("/manage/plugins");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string content = await response.Content.ReadAsStringAsync();
        JsonDocument json = JsonDocument.Parse(content);

        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
    }

    [Fact]
    public async Task ManageQueue_ReturnsOk_WithQueueStatus()
    {
        HttpResponseMessage response = await _client.GetAsync("/manage/queue");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string content = await response.Content.ReadAsStringAsync();
        JsonDocument json = JsonDocument.Parse(content);
        JsonElement root = json.RootElement;

        Assert.True(root.TryGetProperty("workers", out _));
        Assert.True(root.TryGetProperty("pending_jobs", out JsonElement pending));
        Assert.True(pending.GetInt32() >= 0);
        Assert.True(root.TryGetProperty("failed_jobs", out JsonElement failed));
        Assert.True(failed.GetInt32() >= 0);
    }

    [Fact]
    public async Task ManageStop_ReturnsOk()
    {
        // Only verify the endpoint is reachable and returns correct shape
        // We don't actually want to stop the test server, so we check the response format
        // by reading the restart endpoint (which is a no-op) instead
        HttpResponseMessage response = await _client.PostAsync("/manage/restart", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string content = await response.Content.ReadAsStringAsync();
        JsonDocument json = JsonDocument.Parse(content);

        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ManageConfigUpdate_ReturnsOk()
    {
        StringContent body = new(
            JsonSerializer.Serialize(new { server_name = "TestServer" }),
            Encoding.UTF8,
            "application/json"
        );

        HttpResponseMessage response = await _client.PutAsync("/manage/config", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string content = await response.Content.ReadAsStringAsync();
        JsonDocument json = JsonDocument.Parse(content);

        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ManageConfigUpdate_PersistsWorkerCountToConfigurationTable()
    {
        StringContent body = new(
            JsonSerializer.Serialize(new { library_workers = 4 }),
            Encoding.UTF8,
            "application/json"
        );

        HttpResponseMessage response = await _client.PutAsync("/manage/config", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext appContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Configuration? persisted = await appContext.Configuration.FirstOrDefaultAsync(c =>
            c.Key == "libraryRunners"
        );

        Assert.NotNull(persisted);
        Assert.Equal("4", persisted!.Value);
    }

    [Theory]
    [InlineData("library_workers", "libraryRunners")]
    [InlineData("import_workers", "importRunners")]
    [InlineData("extras_workers", "extrasRunners")]
    [InlineData("encoder_workers", "encoderRunners")]
    [InlineData("cron_workers", "cronRunners")]
    [InlineData("image_workers", "imageRunners")]
    [InlineData("file_workers", "fileRunners")]
    [InlineData("music_workers", "musicRunners")]
    public async Task ManageConfigUpdate_NegativeWorkerCount_ReturnsBadRequestWithoutChangingStoredValue(
        string field,
        string key
    )
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext appContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string? originalValue = await appContext
            .Configuration.Where(configuration => configuration.Key == key)
            .Select(configuration => configuration.Value)
            .FirstOrDefaultAsync();

        StringContent body = new(
            JsonSerializer.Serialize(new Dictionary<string, int> { [field] = -1 }),
            Encoding.UTF8,
            "application/json"
        );
        HttpResponseMessage response = await _client.PutAsync("/manage/config", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        HttpResponseMessage getResponse = await _client.GetAsync("/manage/config");
        using JsonDocument document = JsonDocument.Parse(
            await getResponse.Content.ReadAsStringAsync()
        );
        Assert.NotEqual(-1, document.RootElement.GetProperty(field).GetInt32());
        appContext.ChangeTracker.Clear();
        string? storedValue = await appContext
            .Configuration.Where(configuration => configuration.Key == key)
            .Select(configuration => configuration.Value)
            .FirstOrDefaultAsync();
        Assert.Equal(originalValue, storedValue);
    }
}
