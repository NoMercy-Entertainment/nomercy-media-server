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

using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Encoder;
using NoMercy.Events;
using NoMercy.Events.Encoding;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.Tests.Common;

namespace NoMercy.Tests.MediaProcessing.Jobs;

[Collection("EventBusProvider")]
public class FinalizeFailureTests
{
    [Theory]
    [InlineData("tempDir '{TempDir}' missing or empty", "ReportFinalizeFailureAsync")]
    [InlineData("if (resolveFailed)", "ReportFinalizeFailureAsync")]
    [InlineData("if (mergedPlan is null)", "ReportFinalizeFailureAsync")]
    [InlineData("finalizeProfile = PresetResolver.Resolve(", "ReportFinalizeFailureAsync")]
    public void FinalizeFailureBranch_ReportsBeforeReturning(string branch, string expectedCall)
    {
        string source = File.ReadAllText(RepoPaths.SourceFile("VideoEncodeJob.cs"));
        int methodStart = source.IndexOf(
            "private async Task HandleFinalizeAsync",
            StringComparison.Ordinal
        );
        methodStart.Should().BeGreaterThan(0);
        string method = source[methodStart..];
        int branchStart = method.IndexOf(branch, StringComparison.Ordinal);
        branchStart.Should().BeGreaterThan(0);
        int returnAt = method.IndexOf("return;", branchStart, StringComparison.Ordinal);
        returnAt.Should().BeGreaterThan(branchStart);
        method[branchStart..returnAt].Should().Contain(expectedCall);
    }

    [Theory]
    [InlineData("if (folder is null)")]
    [InlineData("if (!fileMetadata.Success)")]
    public void FinalizeWithoutIdentity_ThrowsForQueueRetry(string branch)
    {
        string source = File.ReadAllText(RepoPaths.SourceFile("VideoEncodeJob.cs"));
        int methodStart = source.IndexOf(
            "private async Task HandleFinalizeAsync",
            StringComparison.Ordinal
        );
        string method = source[methodStart..];
        int branchStart = method.IndexOf(branch, StringComparison.Ordinal);
        branchStart.Should().BeGreaterThan(0);
        int nextStatement = method.IndexOf("return;", branchStart, StringComparison.Ordinal);
        method[branchStart..nextStatement].Should().Contain("throw new InvalidOperationException");
    }

    [Fact]
    public async Task EmptyTempFailure_PublishesFailedCardAndRecordsIncompleteEncode()
    {
        IEventBus? previous = (IEventBus?)
            typeof(EventBusProvider)
                .GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null);
        SqliteConnection connection = new("DataSource=:memory:");
        await connection.OpenAsync();
        try
        {
            DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
                .UseSqlite(connection)
                .Options;
            await using MediaContext context = new(options);
            await context.Database.EnsureCreatedAsync();
            await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");

            EncodingStageChangedEvent? stage = null;
            EncodingFailedEvent? failed = null;
            Mock<IEventBus> bus = new();
            bus.Setup(b =>
                    b.PublishAsync(
                        It.IsAny<EncodingStageChangedEvent>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Callback<EncodingStageChangedEvent, CancellationToken>((e, _) => stage = e)
                .Returns(Task.CompletedTask);
            bus.Setup(b =>
                    b.PublishAsync(It.IsAny<EncodingFailedEvent>(), It.IsAny<CancellationToken>())
                )
                .Callback<EncodingFailedEvent, CancellationToken>((e, _) => failed = e)
                .Returns(Task.CompletedTask);
            EventBusProvider.Configure(bus.Object);

            await VideoEncodeJob.ReportFinalizeFailureAsync(
                context,
                new VideoEncodeJob.FileMetadata { Id = 217, Title = "Test encode" },
                "folder-217",
                "/input/movie.mkv",
                "Shared temp folder missing or empty",
                "FinalizeOutputMissing"
            );

            stage.Should().NotBeNull();
            stage!.JobId.Should().Be(217);
            stage.Status.Should().Be("failed");
            failed.Should().NotBeNull();
            failed!.JobId.Should().Be(217);
            IncompleteEncode? row = await context
                .IncompleteEncodes.AsNoTracking()
                .SingleOrDefaultAsync();
            row.Should().NotBeNull();
            row!.MissingRenditions.Should().Be("finalize");
            row.LastError.Should().Be("Shared temp folder missing or empty");
        }
        finally
        {
            typeof(EventBusProvider)
                .GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, previous);
            await connection.DisposeAsync();
        }
    }
}
