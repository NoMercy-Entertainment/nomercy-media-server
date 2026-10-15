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
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Storage;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

[Trait("Category", "DashboardServer")]
public sealed class ServerControllerMusicAddFilesTests
    : IClassFixture<NoMercyApiFactory>,
        IAsyncLifetime
{
    private readonly HttpClient _client;
    private readonly Ulid _libraryId = Ulid.NewUlid();
    private readonly Ulid _folderId = Ulid.NewUlid();

    public ServerControllerMusicAddFilesTests(NoMercyApiFactory factory) =>
        _client = factory.CreateClient().AsAuthenticated();

    public async Task InitializeAsync()
    {
        await using MediaContext context = new();
        context.Libraries.Add(
            new Library
            {
                Id = _libraryId,
                Title = "Manual Music",
                Type = "music",
                AutoEncodeOnScan = false,
            }
        );
        context.Folders.Add(
            new Folder
            {
                Id = _folderId,
                DriverId = Driver.SystemLocalDriverId,
                Path = $"/media/music/{_folderId}",
            }
        );
        context.FolderLibrary.Add(new(_folderId, _libraryId));
        await context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using MediaContext context = new();
        await context.Libraries.Where(library => library.Id == _libraryId).ExecuteDeleteAsync();
        await context.Folders.Where(folder => folder.Id == _folderId).ExecuteDeleteAsync();
        _client.Dispose();
    }

    private List<string> QueuedPayloads()
    {
        using QueueContext context = new();
        return context
            .QueueJobs.AsNoTracking()
            .Where(job => job.Payload.Contains(_libraryId.ToString()))
            .Select(job => job.Payload)
            .ToList();
    }

    private Task<HttpResponseMessage> AddAsync(params object[] files) =>
        _client.PostAsJsonAsync(
            "/api/v1/dashboard/server/addfiles",
            new
            {
                library_id = _libraryId.ToString(),
                folder_id = _folderId.ToString(),
                files,
            }
        );

    [Fact]
    public async Task AcceptedMusicImportReportsSuccessAndPreservesThePluginResponseFields()
    {
        Guid releaseId = Guid.NewGuid();
        string source = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "Music Unsorted", _libraryId.ToString())
        );
        using HttpResponseMessage response = await AddAsync(
            new { id = releaseId.ToString(), path = source }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetString().Should().Be("ok");
        body.RootElement.GetProperty("library_id").GetString().Should().Be(_libraryId.ToString());
        body.RootElement.GetProperty("folder_id").GetString().Should().Be(_folderId.ToString());
        body.RootElement.GetProperty("files")[0]
            .GetProperty("path")
            .GetString()
            .Should()
            .Be(source);
        string payload = QueuedPayloads().Should().ContainSingle().Subject;
        payload
            .Should()
            .Contain("ReleaseImportJob")
            .And.Contain(releaseId.ToString())
            .And.Contain(_folderId.ToString());
        using JsonDocument job = JsonDocument.Parse(payload);
        job.RootElement.GetProperty("inputFolder").GetString().Should().Be(source);
    }

    [Fact]
    public async Task EverySelectedAlbumGetsItsOwnChosenReleaseAndSource()
    {
        Guid first = Guid.NewGuid(),
            second = Guid.NewGuid();
        string firstPath = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "Music Unsorted", "first")
        );
        string secondPath = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "Incoming", "second")
        );
        using HttpResponseMessage response = await AddAsync(
            new { id = first.ToString(), path = firstPath },
            new { id = second.ToString(), path = secondPath }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<string> payloads = QueuedPayloads();
        payloads.Should().HaveCount(2);
        payloads.Should().ContainSingle(payload => payload.Contains(first.ToString()));
        payloads.Should().ContainSingle(payload => payload.Contains(second.ToString()));
        List<string?> sources = [];
        foreach (string payload in payloads)
        {
            using JsonDocument job = JsonDocument.Parse(payload);
            sources.Add(job.RootElement.GetProperty("inputFolder").GetString());
        }
        sources.Should().BeEquivalentTo(firstPath, secondPath);
    }

    [Fact]
    public async Task EmptySelectionIsRefusedClearlyWithoutQueuingAnImport()
    {
        using HttpResponseMessage response = await AddAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("detail").GetString().Should().Contain("Select");
        QueuedPayloads().Should().BeEmpty();
    }

    [Fact]
    public async Task InvalidLaterSourceCannotLeaveTheFirstAlbumQueued()
    {
        using HttpResponseMessage response = await AddAsync(
            new { id = Guid.NewGuid().ToString(), path = Path.GetFullPath(Path.GetTempPath()) },
            new { id = Guid.NewGuid().ToString(), path = "" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        QueuedPayloads().Should().BeEmpty();
    }
}
