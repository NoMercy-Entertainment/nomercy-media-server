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
using Microsoft.Extensions.DependencyInjection;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Storage;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

[Trait("Category", "DashboardLibraries")]
public class DashboardLibrariesControllerTests : IClassFixture<NoMercyApiFactory>
{
    private readonly HttpClient _authed;
    private readonly HttpClient _unauthed;
    private readonly NoMercyApiFactory _factory;

    public DashboardLibrariesControllerTests(NoMercyApiFactory factory)
    {
        _factory = factory;
        _authed = factory.CreateClient().AsAuthenticated();
        _unauthed = factory.CreateClient().AsUnauthenticated();
    }

    private static StringContent JsonBody(object obj) =>
        new(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> PatchAsync(HttpClient client, string url, object body) =>
        client.PatchAsync(url, JsonBody(body));

    private Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, object body) =>
        client.PostAsync(url, JsonBody(body));

    [Fact]
    public async Task GetLibraries_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.GetAsync("/api/v1/dashboard/libraries");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetLibraries_ReturnsOk_WhenAuthenticated()
    {
        HttpResponseMessage response = await _authed.GetAsync("/api/v1/dashboard/libraries");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetLibraries_ReturnsEnvelopeWithDataArray()
    {
        HttpResponseMessage response = await _authed.GetAsync("/api/v1/dashboard/libraries");

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        doc.RootElement.TryGetProperty("data", out JsonElement data)
            .Should()
            .BeTrue("dashboard libraries response must have a 'data' property");
        data.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task GetLibraries_DataItems_ContainRequiredFields()
    {
        HttpResponseMessage response = await _authed.GetAsync("/api/v1/dashboard/libraries");

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        JsonElement data = doc.RootElement.GetProperty("data");
        data.GetArrayLength().Should().BeGreaterThan(0, "seed data has at least 3 libraries");

        JsonElement item = data.EnumerateArray().First();

        item.TryGetProperty("id", out _).Should().BeTrue("dashboard library item must have 'id'");
        item.TryGetProperty("title", out _)
            .Should()
            .BeTrue("dashboard library item must have 'title'");
        item.TryGetProperty("type", out _)
            .Should()
            .BeTrue("dashboard library item must have 'type'");
        item.TryGetProperty("folder_library", out JsonElement folderLibrary)
            .Should()
            .BeTrue("dashboard library item must have 'folder_library'");
        folderLibrary.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task GetLibraries_DataItems_HavePaginationAndLinkFields()
    {
        HttpResponseMessage response = await _authed.GetAsync("/api/v1/dashboard/libraries");

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        JsonElement data = doc.RootElement.GetProperty("data");
        JsonElement item = data.EnumerateArray().First();

        item.TryGetProperty("pagination", out _)
            .Should()
            .BeTrue("dashboard library item must expose 'pagination' for client routing decisions");
        item.TryGetProperty("link", out _)
            .Should()
            .BeTrue("dashboard library item must expose 'link' for client navigation");
    }

    [Fact]
    public async Task PostLibrary_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.PostAsync(
            "/api/v1/dashboard/libraries",
            null
        );

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PostLibrary_ReturnsOkWithStatusAndData_WhenAuthenticated()
    {
        HttpResponseMessage response = await _authed.PostAsync("/api/v1/dashboard/libraries", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        doc.RootElement.TryGetProperty("status", out JsonElement status)
            .Should()
            .BeTrue("create-library response must have a 'status' field");
        status.GetString().Should().Be("ok");

        doc.RootElement.TryGetProperty("data", out JsonElement data)
            .Should()
            .BeTrue("create-library response must return the created library in 'data'");
        data.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task PostLibrary_NamesNewLibraryFromOneBasedCount()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ILibraryRepository libraryRepository =
            scope.ServiceProvider.GetRequiredService<ILibraryRepository>();
        int countBefore = await libraryRepository.CountAsync();

        HttpResponseMessage response = await _authed.PostAsync("/api/v1/dashboard/libraries", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data")
            .GetProperty("title")
            .GetString()
            .Should()
            .Be($"Library {countBefore + 1}");
    }

    [Fact]
    public async Task PatchLibrary_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.PatchAsync(
            $"/api/v1/dashboard/libraries/{NoMercyApiFactory.MovieLibraryId}",
            null
        );

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PatchLibrary_ReturnsNotFound_WhenLibraryDoesNotExist()
    {
        Ulid nonExistentId = Ulid.NewUlid();
        HttpResponseMessage response = await PatchAsync(
            _authed,
            $"/api/v1/dashboard/libraries/{nonExistentId}",
            new { title = "Test" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchLibrary_WithAutoEncodeFields_PersistsBothValues()
    {
        Ulid libraryId = await SeedIsolatedLibraryAsync();
        Ulid presetId = await SeedEncodingPresetAsync();

        HttpResponseMessage response = await PatchAsync(
            _authed,
            $"/api/v1/dashboard/libraries/{libraryId}",
            new { autoEncodeOnScan = true, encodePresetId = presetId.ToString() }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        Library reloaded = await LoadLibraryAsync(libraryId);
        reloaded.AutoEncodeOnScan.Should().BeTrue();
        reloaded.EncodePresetId.Should().Be(presetId);
    }

    [Fact]
    public async Task PatchLibrary_OmittingAutoEncodeFields_LeavesExistingValuesUnchanged()
    {
        Ulid presetId = await SeedEncodingPresetAsync();
        Ulid libraryId = await SeedIsolatedLibraryAsync(
            autoEncodeOnScan: true,
            encodePresetId: presetId
        );

        HttpResponseMessage response = await PatchAsync(
            _authed,
            $"/api/v1/dashboard/libraries/{libraryId}",
            new { title = "Renamed only" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        Library reloaded = await LoadLibraryAsync(libraryId);
        reloaded.Title.Should().Be("Renamed only");
        reloaded.AutoEncodeOnScan.Should().BeTrue();
        reloaded.EncodePresetId.Should().Be(presetId);
    }

    [Fact]
    public async Task PatchLibrary_WithUnknownEncodePresetId_ReturnsNotFound_AndDoesNotChangeLibrary()
    {
        Ulid libraryId = await SeedIsolatedLibraryAsync();
        Ulid unknownPresetId = Ulid.NewUlid();

        HttpResponseMessage response = await PatchAsync(
            _authed,
            $"/api/v1/dashboard/libraries/{libraryId}",
            new { autoEncodeOnScan = true, encodePresetId = unknownPresetId.ToString() }
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        Library reloaded = await LoadLibraryAsync(libraryId);
        reloaded.AutoEncodeOnScan.Should().BeFalse();
        reloaded.EncodePresetId.Should().BeNull();
    }

    [Fact]
    public async Task PatchLibrary_WithImage_PersistsTheImage()
    {
        Ulid libraryId = await SeedIsolatedLibraryAsync();

        HttpResponseMessage response = await PatchAsync(
            _authed,
            $"/api/v1/dashboard/libraries/{libraryId}",
            new { image = "/library-images/new-cover.jpg" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        Library reloaded = await LoadLibraryAsync(libraryId);
        reloaded.Image.Should().Be("/library-images/new-cover.jpg");
    }

    [Fact]
    public async Task PatchLibrary_WithEmptyImage_RemovesThePersistedImage()
    {
        Ulid libraryId = await SeedIsolatedLibraryAsync();

        HttpResponseMessage setResponse = await PatchAsync(
            _authed,
            $"/api/v1/dashboard/libraries/{libraryId}",
            new { image = "/library-images/old-cover.jpg" }
        );
        setResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoadLibraryAsync(libraryId)).Image.Should().Be("/library-images/old-cover.jpg");

        HttpResponseMessage clearResponse = await PatchAsync(
            _authed,
            $"/api/v1/dashboard/libraries/{libraryId}",
            new { image = "" }
        );
        clearResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        Library reloaded = await LoadLibraryAsync(libraryId);
        reloaded
            .Image.Should()
            .BeNull("an empty image value in the request must clear the library's image");
    }

    [Fact]
    public async Task DeleteLibrary_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.DeleteAsync(
            $"/api/v1/dashboard/libraries/{NoMercyApiFactory.MovieLibraryId}"
        );

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DeleteLibrary_ReturnsNotFound_WhenLibraryDoesNotExist()
    {
        Ulid nonExistentId = Ulid.NewUlid();
        HttpResponseMessage response = await _authed.DeleteAsync(
            $"/api/v1/dashboard/libraries/{nonExistentId}"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RescanAll_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.PostAsync(
            "/api/v1/dashboard/libraries/rescan",
            null
        );

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RescanAll_ReturnsOkWithStatusField_WhenAuthenticated()
    {
        HttpResponseMessage response = await _authed.PostAsync(
            "/api/v1/dashboard/libraries/rescan",
            null
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        doc.RootElement.TryGetProperty("status", out JsonElement status)
            .Should()
            .BeTrue("rescan response must include 'status'");
        status.GetString().Should().Be("ok");
    }

    [Fact]
    public async Task RefreshAll_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.PostAsync(
            "/api/v1/dashboard/libraries/refresh",
            null
        );

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RefreshAll_ReturnsOkWithStatusField_WhenAuthenticated()
    {
        HttpResponseMessage response = await _authed.PostAsync(
            "/api/v1/dashboard/libraries/refresh",
            null
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        doc.RootElement.TryGetProperty("status", out JsonElement status)
            .Should()
            .BeTrue("refresh response must include 'status'");
        status.GetString().Should().Be("ok");
    }

    [Fact]
    public async Task RescanOne_ReturnsNotFound_WhenLibraryDoesNotExist()
    {
        Ulid nonExistentId = Ulid.NewUlid();
        HttpResponseMessage response = await _authed.PostAsync(
            $"/api/v1/dashboard/libraries/{nonExistentId}/rescan",
            null
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AddFolder_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await PostJsonAsync(
            _unauthed,
            $"/api/v1/dashboard/libraries/{NoMercyApiFactory.MovieLibraryId}/folders",
            new { path = "/test", driverId = Ulid.NewUlid().ToString() }
        );

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    // Adding a brand-new folder full of pre-existing media must import that
    // media automatically (critical-path #5) — the controller dispatches
    // a LibraryScanJob for the library the folder was just attached to.
    // Every assertion below targets a library seeded fresh inside the test
    // (never one of the shared fixture libraries), so a stray scan dispatched
    // by an unrelated test can't produce a false pass/fail here.
    [Fact]
    public async Task AddFolder_NewFolder_DispatchesLibraryScanJob()
    {
        Ulid libraryId = await SeedIsolatedLibraryAsync();
        string path = $"/media/new-folder-scan-{Ulid.NewUlid()}";

        HttpResponseMessage response = await PostJsonAsync(
            _authed,
            $"/api/v1/dashboard/libraries/{libraryId}/folders",
            new { path, driver_id = Driver.SystemLocalDriverId.ToString() }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        CountDispatchedLibraryScanJobs(libraryId)
            .Should()
            .Be(1, "a genuinely new folder must trigger exactly one scan of its library");
    }

    [Fact]
    public async Task AddFolder_MessageArgumentNamesLibrary()
    {
        Ulid libraryId = await SeedIsolatedLibraryAsync();
        Library library = await LoadLibraryAsync(libraryId);
        string path = $"/media/message-folder-{Ulid.NewUlid()}";

        HttpResponseMessage response = await PostJsonAsync(
            _authed,
            $"/api/v1/dashboard/libraries/{libraryId}/folders",
            new { path, driver_id = Driver.SystemLocalDriverId.ToString() }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("message")
            .GetString()
            .Should()
            .Be("Successfully added folder to {0} library.");
        JsonElement args = doc.RootElement.GetProperty("args");
        args.GetArrayLength().Should().Be(1);
        args[0].GetString().Should().Be(library.Title);
    }

    [Fact]
    public async Task AddFolder_PreExistingFolder_DoesNotDispatchLibraryScanJob()
    {
        Ulid firstLibraryId = await SeedIsolatedLibraryAsync();
        Ulid secondLibraryId = await SeedIsolatedLibraryAsync();
        string sharedPath = $"/media/shared-folder-{Ulid.NewUlid()}";

        // Arrange: attach the folder to the first library. It is genuinely
        // new here, so this call is expected to dispatch a scan — that scan
        // is the baseline proving the queue/DB plumbing works, not what this
        // test is asserting on.
        HttpResponseMessage firstResponse = await PostJsonAsync(
            _authed,
            $"/api/v1/dashboard/libraries/{firstLibraryId}/folders",
            new { path = sharedPath, driver_id = Driver.SystemLocalDriverId.ToString() }
        );
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        CountDispatchedLibraryScanJobs(firstLibraryId).Should().Be(1);

        // Act: attach the SAME driver+path (an already-known folder) to a
        // second, never-touched library. The compat requirement is that
        // re-attaching an existing folder must never trigger a scan.
        HttpResponseMessage secondResponse = await PostJsonAsync(
            _authed,
            $"/api/v1/dashboard/libraries/{secondLibraryId}/folders",
            new { path = sharedPath, driver_id = Driver.SystemLocalDriverId.ToString() }
        );

        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        CountDispatchedLibraryScanJobs(secondLibraryId)
            .Should()
            .Be(0, "attaching an already-known folder must never dispatch a new scan");
    }

    private static async Task<Ulid> SeedIsolatedLibraryAsync(
        bool autoEncodeOnScan = false,
        Ulid? encodePresetId = null
    )
    {
        Ulid libraryId = Ulid.NewUlid();

        await using MediaContext mediaContext = new();
        mediaContext.Libraries.Add(
            new()
            {
                Id = libraryId,
                Title = $"Isolated Library {libraryId}",
                Type = "movie",
                Order = 99,
                AutoEncodeOnScan = autoEncodeOnScan,
                EncodePresetId = encodePresetId,
            }
        );
        await mediaContext.SaveChangesAsync();

        return libraryId;
    }

    private static async Task<Ulid> SeedEncodingPresetAsync()
    {
        Ulid presetId = Ulid.NewUlid();

        await using MediaContext mediaContext = new();
        mediaContext.EncodingPresets.Add(
            new()
            {
                Id = presetId,
                Name = $"Preset {presetId}",
                ProfileJson = "{}",
                IsBuiltIn = false,
            }
        );
        await mediaContext.SaveChangesAsync();

        return presetId;
    }

    private static async Task<Library> LoadLibraryAsync(Ulid libraryId)
    {
        await using MediaContext mediaContext = new();
        Library? library = await mediaContext.Libraries.FindAsync(libraryId);
        library.Should().NotBeNull("the library seeded for this test must still exist");
        return library!;
    }

    private static int CountDispatchedLibraryScanJobs(Ulid libraryId)
    {
        using QueueContext queueContext = new();
        return queueContext
            .QueueJobs.AsEnumerable()
            .Count(job =>
                job.Payload.Contains("LibraryScanJob") && job.Payload.Contains(libraryId.ToString())
            );
    }
}
