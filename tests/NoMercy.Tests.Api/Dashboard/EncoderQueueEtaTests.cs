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
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using NoMercy.Api.Controllers.V1.Dashboard.Admin;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Queue;
using NoMercy.NmSystem.Configuration;
using NoMercy.Queue.MediaServer.Repositories;
using NoMercy.Service;
using NoMercy.Tests.Api.Infrastructure;
using NoMercyQueue.Core;
using NoMercyQueue.Core.Models;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

[Trait("Category", "Unit")]
public class EncoderQueueEtaTests : IClassFixture<NoMercyApiFactory>, IAsyncLifetime
{
    private readonly NoMercyApiFactory _factory;
    private readonly string _folder = $"/eta-tests/{Ulid.NewUlid()}";
    private readonly List<int> _queueRowIds = [];
    private readonly List<Ulid> _videoFileIds = [];
    private readonly Ulid _historyId = Ulid.NewUlid();

    public EncoderQueueEtaTests(NoMercyApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await using MediaContext media = new();
        media.EncodingHistory.Add(
            new()
            {
                Id = _historyId,
                InputPath = $"{_folder}/history.mkv",
                OutputPath = $"{_folder}/history.mp4",
                ProfileName = "ETA test",
                EncoderUsed = "libx264",
                DurationSeconds = 100,
                AverageSpeed = 2,
                CreatedAt = DateTime.UtcNow.AddYears(1),
            }
        );

        foreach (
            (string filename, string duration) in new[]
            {
                ("equal-a.mkv", "00:03:20"),
                ("equal-b.mkv", "00:03:20"),
                ("short.mkv", "00:01:00"),
                ("long.mkv", "00:03:00"),
            }
        )
        {
            VideoFile file = new()
            {
                HostFolder = _folder,
                Filename = filename,
                Duration = duration,
            };
            media.VideoFiles.Add(file);
            _videoFileIds.Add(file.Id);
        }

        await media.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using QueueContext queue = new();
        await queue.QueueJobs.Where(row => _queueRowIds.Contains(row.Id)).ExecuteDeleteAsync();

        await using MediaContext media = new();
        await media.VideoFiles.Where(file => _videoFileIds.Contains(file.Id)).ExecuteDeleteAsync();
        await media.EncodingHistory.Where(row => row.Id == _historyId).ExecuteDeleteAsync();
    }

    private async Task<double> GetEstimateAsync(int workers, params string[] filenames)
    {
        KeyValuePair<string, int> previousWorkers = RuntimeServerSettings.Current.EncoderWorkers;
        try
        {
            RuntimeServerSettings.Current.EncoderWorkers = new(QueueNames.Encoder, workers);
            await using QueueContext queue = new();
            foreach (string filename in filenames)
            {
                string inputPath = $"{_folder}/{filename}";
                QueueJob row = new()
                {
                    Queue = QueueNames.Encoder,
                    Priority = 4,
                    Payload = $$"""
                        {
                          "$type": "NoMercy.MediaProcessing.Jobs.MediaJobs.VideoEncodeJob, NoMercy.MediaProcessing",
                          "queueName": "encoder",
                          "priority": 4,
                          "id": "{{Ulid.NewUlid()}}",
                          "inputFile": "{{inputPath}}"
                        }
                        """,
                    AvailableAt = DateTime.UtcNow,
                };
                queue.QueueJobs.Add(row);
                await queue.SaveChangesAsync();
                _queueRowIds.Add(row.Id);
            }

            using IServiceScope scope = _factory.Services.CreateScope();
            IQueueTaskRepository realQueueRepository =
                scope.ServiceProvider.GetRequiredService<IQueueTaskRepository>();
            List<QueueJobModel> testRows =
            [
                .. (await realQueueRepository.GetEncoderQueueJobsAsync()).Where(row =>
                    _queueRowIds.Contains(row.Id)
                ),
            ];
            testRows.Should().HaveCount(filenames.Length);

            // The shared test DB retains rows from other dashboard tests. The
            // endpoint sees only these real repository rows for an exact ETA.
            Mock<IQueueTaskRepository> queueRepository = new();
            queueRepository
                .Setup(repository =>
                    repository.GetEncoderQueueJobsAsync(It.IsAny<CancellationToken>())
                )
                .ReturnsAsync(testRows);
            using WebApplicationFactory<Startup> host = _factory.WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IQueueTaskRepository>();
                    services.AddScoped(_ => queueRepository.Object);
                })
            );
            using HttpClient authed = host.CreateClient().AsAuthenticated();
            HttpResponseMessage response = await authed.GetAsync(
                "/api/v1/dashboard/tasks/queue/eta"
            );
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            queueRepository.Verify(
                repository => repository.GetEncoderQueueJobsAsync(It.IsAny<CancellationToken>()),
                Times.Once
            );
            using JsonDocument document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync()
            );
            JsonElement body = document.RootElement;
            body.GetProperty("queueDepth").GetInt32().Should().Be(filenames.Length);
            body.GetProperty("basedOnSamples").GetInt32().Should().Be(1);
            body.GetProperty("averageEncodeSeconds").GetDouble().Should().Be(100);
            return body.GetProperty("estimatedSecondsRemaining").GetDouble();
        }
        finally
        {
            RuntimeServerSettings.Current.EncoderWorkers = previousWorkers;
        }
    }

    [Fact]
    public async Task Endpoint_TwoWorkersFinishEqualDurationJobsInOneItemTime()
    {
        double estimate = await GetEstimateAsync(2, "equal-a.mkv", "equal-b.mkv");
        estimate.Should().Be(100);
    }

    [Fact]
    public async Task Endpoint_OneWorkerAddsEachTitlesDuration()
    {
        double estimate = await GetEstimateAsync(1, "long.mkv", "short.mkv");
        estimate.Should().Be(120);
    }

    [Fact]
    public async Task Endpoint_UnknownInputPathUsesHistoricalPerItemAverage()
    {
        double estimate = await GetEstimateAsync(1, "missing.mkv");
        estimate.Should().Be(100);
    }

    [Fact]
    public void TwoWorkersFinishEqualItemsInHalfTheTime()
    {
        double oneWorker = TasksController.EstimateQueueSeconds([100, 100], 1);
        double twoWorkers = TasksController.EstimateQueueSeconds([100, 100], 2);

        twoWorkers.Should().Be(oneWorker / 2);
    }

    [Fact]
    public void LongerTitleTakesLongerThanShorterTitle()
    {
        double shortTitle = TasksController.EstimateItemSeconds(60, 2, 100);
        double longTitle = TasksController.EstimateItemSeconds(180, 2, 100);

        longTitle.Should().Be(shortTitle * 3);
    }

    [Fact]
    public void UnknownTitleDurationUsesHistoricalPerItemAverage()
    {
        TasksController.EstimateItemSeconds(null, 2, 100).Should().Be(100);
        TasksController.EstimateItemSeconds(60, 0, 100).Should().Be(100);
    }
}
