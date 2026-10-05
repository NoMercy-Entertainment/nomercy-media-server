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

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Users;

namespace NoMercy.Tests.Repositories;

// ActivityRepository queries Include() the ActivityLog.Device and .User navigations,
// both of which are modeled as required references. On a required relationship EF
// Core's single-query Include compiles to an INNER JOIN, so any ActivityLog row whose
// DeviceId/UserId does not resolve to a seeded Device/User silently vanishes from the
// result set -- not a thrown FK violation, just an empty list. Every GetPagedAsync test
// below seeds its Device/User rows first for that reason.
//
// CreatedAt is `DatabaseGenerated(Computed)` with a SQLite `CURRENT_TIMESTAMP` column
// default: the app never writes it on insert, so it is always "real wall-clock now",
// independent of ActivityLog.Time (the domain field the test scenarios set). Since
// GetPagedAsync/DeleteAsync filter and order by CreatedAt, tests that assert a date-based
// requirement pin CreatedAt directly with a raw UPDATE after insert -- otherwise the
// assertion is decoupled from what the repository actually queries and passes or fails by
// coincidence of wall-clock timing.
public class ActivityRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public ActivityRepositoryTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        using (SqliteCommand fkOff = _connection.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
            fkOff.ExecuteNonQuery();
        }

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext ctx = new(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private static async Task SetupUsersAndDevicesAsync(
        MediaContext context,
        IReadOnlyCollection<Guid> userIds,
        IReadOnlyCollection<Ulid> deviceIds
    )
    {
        foreach (Guid userId in userIds)
            context.Users.Add(new() { Id = userId, Email = $"{userId}@example.com" });

        foreach (Ulid deviceId in deviceIds)
            context.Devices.Add(
                new()
                {
                    Id = deviceId,
                    DeviceId = deviceId.ToString(),
                    Type = "web",
                    Browser = "Chrome",
                    Os = "Windows",
                    Model = "Desktop",
                }
            );

        await context.SaveChangesAsync();
    }

    private static Task SetupDeviceAndUser(MediaContext context, Guid userId, Ulid deviceId)
    {
        return SetupUsersAndDevicesAsync(context, [userId], [deviceId]);
    }

    // Overwrites the DB-generated CreatedAt column directly, bypassing the
    // DatabaseGeneratedOption.Computed convention that makes EF ignore the property
    // on INSERT. This is the only way to give a test deterministic control over the
    // column the repository actually filters/orders on.
    private static Task PinCreatedAtAsync(
        MediaContext context,
        int activityLogId,
        DateTime createdAt
    )
    {
        return context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ActivityLogs SET CreatedAt = {createdAt} WHERE Id = {activityLogId}"
        );
    }

    [Fact]
    public async Task GetPagedAsync_filters_by_category_and_excludes_other_categories()
    {
        using MediaContext context = new(_options);
        Guid userId = Guid.NewGuid();
        Ulid deviceId = Ulid.NewUlid();
        await SetupDeviceAndUser(context, userId, deviceId);

        ActivityRepository repository = new(context);

        ActivityLog authLog = new()
        {
            Category = ActivityCategory.Auth,
            Type = "auth.login",
            UserId = userId,
            DeviceId = deviceId,
            Time = DateTime.UtcNow,
        };

        ActivityLog playbackLog = new()
        {
            Category = ActivityCategory.Playback,
            Type = "playback.started",
            UserId = userId,
            DeviceId = deviceId,
            Time = DateTime.UtcNow,
        };

        context.ActivityLogs.AddRange(authLog, playbackLog);
        await context.SaveChangesAsync();

        List<ActivityLog> result = await repository.GetPagedAsync(
            category: ActivityCategory.Auth,
            userId: null,
            deviceId: null,
            mediaId: null,
            from: null,
            to: null,
            success: null,
            skip: 0,
            take: 10
        );

        result.Should().ContainSingle();
        result[0].Category.Should().Be(ActivityCategory.Auth);
    }

    [Fact]
    public async Task GetPagedAsync_filters_by_user_id_and_excludes_other_users()
    {
        using MediaContext context = new(_options);
        Guid user1 = Guid.NewGuid();
        Guid user2 = Guid.NewGuid();
        Ulid device = Ulid.NewUlid();
        await SetupUsersAndDevicesAsync(context, [user1, user2], [device]);

        ActivityRepository repository = new(context);

        ActivityLog user1Log = new()
        {
            Category = ActivityCategory.Auth,
            Type = "auth.login",
            UserId = user1,
            DeviceId = device,
            Time = DateTime.UtcNow,
        };

        ActivityLog user2Log = new()
        {
            Category = ActivityCategory.Auth,
            Type = "auth.login",
            UserId = user2,
            DeviceId = device,
            Time = DateTime.UtcNow,
        };

        context.ActivityLogs.AddRange(user1Log, user2Log);
        await context.SaveChangesAsync();

        List<ActivityLog> result = await repository.GetPagedAsync(
            category: null,
            userId: user1,
            deviceId: null,
            mediaId: null,
            from: null,
            to: null,
            success: null,
            skip: 0,
            take: 10
        );

        result.Should().ContainSingle();
        result[0].UserId.Should().Be(user1);
    }

    [Fact]
    public async Task GetPagedAsync_filters_by_device_id_and_excludes_other_devices()
    {
        using MediaContext context = new(_options);
        Guid userId = Guid.NewGuid();
        Ulid device1 = Ulid.NewUlid();
        Ulid device2 = Ulid.NewUlid();
        await SetupUsersAndDevicesAsync(context, [userId], [device1, device2]);

        ActivityRepository repository = new(context);

        ActivityLog device1Log = new()
        {
            Category = ActivityCategory.Auth,
            Type = "auth.login",
            UserId = userId,
            DeviceId = device1,
            Time = DateTime.UtcNow,
        };

        ActivityLog device2Log = new()
        {
            Category = ActivityCategory.Auth,
            Type = "auth.login",
            UserId = userId,
            DeviceId = device2,
            Time = DateTime.UtcNow,
        };

        context.ActivityLogs.AddRange(device1Log, device2Log);
        await context.SaveChangesAsync();

        List<ActivityLog> result = await repository.GetPagedAsync(
            category: null,
            userId: null,
            deviceId: device1,
            mediaId: null,
            from: null,
            to: null,
            success: null,
            skip: 0,
            take: 10
        );

        result.Should().ContainSingle();
        result[0].DeviceId.Should().Be(device1);
    }

    [Fact]
    public async Task GetPagedAsync_filters_by_media_id_and_excludes_other_media()
    {
        using MediaContext context = new(_options);
        Guid userId = Guid.NewGuid();
        Ulid device = Ulid.NewUlid();
        await SetupDeviceAndUser(context, userId, device);

        ActivityRepository repository = new(context);

        Ulid media1 = Ulid.NewUlid();
        Ulid media2 = Ulid.NewUlid();

        ActivityLog media1Log = new()
        {
            Category = ActivityCategory.Playback,
            Type = "playback.started",
            UserId = userId,
            DeviceId = device,
            MediaId = media1,
            Time = DateTime.UtcNow,
        };

        ActivityLog media2Log = new()
        {
            Category = ActivityCategory.Playback,
            Type = "playback.started",
            UserId = userId,
            DeviceId = device,
            MediaId = media2,
            Time = DateTime.UtcNow,
        };

        context.ActivityLogs.AddRange(media1Log, media2Log);
        await context.SaveChangesAsync();

        List<ActivityLog> result = await repository.GetPagedAsync(
            category: null,
            userId: null,
            deviceId: null,
            mediaId: media1,
            from: null,
            to: null,
            success: null,
            skip: 0,
            take: 10
        );

        result.Should().ContainSingle();
        result[0].MediaId.Should().Be(media1);
    }

    [Fact]
    public async Task GetPagedAsync_filters_by_date_range_and_excludes_outside_range()
    {
        using MediaContext context = new(_options);
        Guid userId = Guid.NewGuid();
        Ulid device = Ulid.NewUlid();
        await SetupDeviceAndUser(context, userId, device);

        ActivityRepository repository = new(context);

        DateTime anchor = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        DateTime cutoffStart = anchor.AddHours(-1);
        DateTime cutoffEnd = anchor.AddHours(1);

        ActivityLog beforeLog = new()
        {
            Category = ActivityCategory.Auth,
            Type = "auth.login",
            UserId = userId,
            DeviceId = device,
            Time = cutoffStart.AddMinutes(-5),
        };

        ActivityLog withinLog = new()
        {
            Category = ActivityCategory.Auth,
            Type = "auth.login",
            UserId = userId,
            DeviceId = device,
            Time = anchor,
        };

        ActivityLog afterLog = new()
        {
            Category = ActivityCategory.Auth,
            Type = "auth.login",
            UserId = userId,
            DeviceId = device,
            Time = cutoffEnd.AddMinutes(5),
        };

        context.ActivityLogs.AddRange(beforeLog, withinLog, afterLog);
        await context.SaveChangesAsync();

        await PinCreatedAtAsync(context, beforeLog.Id, beforeLog.Time);
        await PinCreatedAtAsync(context, withinLog.Id, withinLog.Time);
        await PinCreatedAtAsync(context, afterLog.Id, afterLog.Time);

        List<ActivityLog> result = await repository.GetPagedAsync(
            category: null,
            userId: null,
            deviceId: null,
            mediaId: null,
            from: cutoffStart,
            to: cutoffEnd,
            success: null,
            skip: 0,
            take: 10
        );

        result.Should().ContainSingle();
        result[0].Id.Should().Be(withinLog.Id);
    }

    [Fact]
    public async Task GetPagedAsync_filters_by_success_and_excludes_failed_logs()
    {
        using MediaContext context = new(_options);
        Guid userId = Guid.NewGuid();
        Ulid device = Ulid.NewUlid();
        await SetupDeviceAndUser(context, userId, device);

        ActivityRepository repository = new(context);

        ActivityLog successLog = new()
        {
            Category = ActivityCategory.Auth,
            Type = "auth.login",
            UserId = userId,
            DeviceId = device,
            Success = true,
            Time = DateTime.UtcNow,
        };

        ActivityLog failLog = new()
        {
            Category = ActivityCategory.Auth,
            Type = "auth.login",
            UserId = userId,
            DeviceId = device,
            Success = false,
            Time = DateTime.UtcNow,
        };

        context.ActivityLogs.AddRange(successLog, failLog);
        await context.SaveChangesAsync();

        List<ActivityLog> result = await repository.GetPagedAsync(
            category: null,
            userId: null,
            deviceId: null,
            mediaId: null,
            from: null,
            to: null,
            success: true,
            skip: 0,
            take: 10
        );

        result.Should().ContainSingle();
        result[0].Success.Should().BeTrue();
    }

    [Fact]
    public async Task GetPagedAsync_orders_by_created_at_descending_then_id_descending()
    {
        using MediaContext context = new(_options);
        Guid userId = Guid.NewGuid();
        Ulid device = Ulid.NewUlid();
        await SetupDeviceAndUser(context, userId, device);

        ActivityRepository repository = new(context);

        DateTime anchor = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        // tieOlder/tieNewer share a CreatedAt instant but tieNewer is inserted (and so
        // gets a higher Id) second -- isolates the `ThenByDescending(Id)` tie-break.
        // newest has a strictly later CreatedAt -- isolates the primary
        // `OrderByDescending(CreatedAt)` sort, independent of insertion/Id order.
        ActivityLog tieOlder = new()
        {
            Category = ActivityCategory.Auth,
            Type = "auth.login",
            UserId = userId,
            DeviceId = device,
            Time = anchor,
        };
        context.ActivityLogs.Add(tieOlder);
        await context.SaveChangesAsync();

        ActivityLog tieNewer = new()
        {
            Category = ActivityCategory.Auth,
            Type = "auth.login",
            UserId = userId,
            DeviceId = device,
            Time = anchor,
        };
        context.ActivityLogs.Add(tieNewer);
        await context.SaveChangesAsync();

        ActivityLog newest = new()
        {
            Category = ActivityCategory.Auth,
            Type = "auth.login",
            UserId = userId,
            DeviceId = device,
            Time = anchor.AddHours(1),
        };
        context.ActivityLogs.Add(newest);
        await context.SaveChangesAsync();

        await PinCreatedAtAsync(context, tieOlder.Id, anchor);
        await PinCreatedAtAsync(context, tieNewer.Id, anchor);
        await PinCreatedAtAsync(context, newest.Id, anchor.AddHours(1));

        List<ActivityLog> result = await repository.GetPagedAsync(
            category: null,
            userId: null,
            deviceId: null,
            mediaId: null,
            from: null,
            to: null,
            success: null,
            skip: 0,
            take: 10
        );

        result.Should().HaveCount(3);
        result[0].Id.Should().Be(newest.Id, "the strictly newer CreatedAt must sort first");
        result[1]
            .Id.Should()
            .Be(tieNewer.Id, "of two equal CreatedAt values, the higher Id must win the tie-break");
        result[2].Id.Should().Be(tieOlder.Id);
    }

    [Fact]
    public async Task GetPagedAsync_respects_skip_and_take_for_pagination()
    {
        using MediaContext context = new(_options);
        Guid userId = Guid.NewGuid();
        Ulid device = Ulid.NewUlid();
        await SetupDeviceAndUser(context, userId, device);

        ActivityRepository repository = new(context);

        for (int i = 0; i < 10; i++)
        {
            ActivityLog log = new()
            {
                Category = ActivityCategory.Auth,
                Type = "auth.login",
                UserId = userId,
                DeviceId = device,
                Time = DateTime.UtcNow,
            };
            context.ActivityLogs.Add(log);
        }

        await context.SaveChangesAsync();

        List<ActivityLog> page1 = await repository.GetPagedAsync(
            category: null,
            userId: null,
            deviceId: null,
            mediaId: null,
            from: null,
            to: null,
            success: null,
            skip: 0,
            take: 3
        );

        List<ActivityLog> page2 = await repository.GetPagedAsync(
            category: null,
            userId: null,
            deviceId: null,
            mediaId: null,
            from: null,
            to: null,
            success: null,
            skip: 3,
            take: 3
        );

        page1.Should().HaveCount(3);
        page2.Should().HaveCount(3);
        page1.Select(l => l.Id).Should().NotIntersectWith(page2.Select(l => l.Id));
    }

    [Fact]
    public async Task GetPagedAsync_uses_no_tracking_for_read_performance()
    {
        using MediaContext context = new(_options);
        Guid userId = Guid.NewGuid();
        Ulid device = Ulid.NewUlid();
        await SetupDeviceAndUser(context, userId, device);

        ActivityRepository repository = new(context);

        ActivityLog log = new()
        {
            Category = ActivityCategory.Auth,
            Type = "auth.login",
            UserId = userId,
            DeviceId = device,
            Time = DateTime.UtcNow,
        };

        context.ActivityLogs.Add(log);
        await context.SaveChangesAsync();

        // SaveChangesAsync itself leaves the just-inserted entity tracked; clear that
        // tracked entry so the assertion below can only pass if GetPagedAsync's own
        // AsNoTracking query is what leaves the ChangeTracker empty.
        context.ChangeTracker.Clear();

        List<ActivityLog> result = await repository.GetPagedAsync(
            category: null,
            userId: null,
            deviceId: null,
            mediaId: null,
            from: null,
            to: null,
            success: null,
            skip: 0,
            take: 10
        );

        result.Should().ContainSingle();
        context.ChangeTracker.Entries().Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_removes_logs_matching_category()
    {
        using MediaContext context = new(_options);
        Guid userId = Guid.NewGuid();
        Ulid device = Ulid.NewUlid();
        await SetupDeviceAndUser(context, userId, device);

        ActivityRepository repository = new(context);

        ActivityLog authLog = new()
        {
            Category = ActivityCategory.Auth,
            Type = "auth.login",
            UserId = userId,
            DeviceId = device,
            Time = DateTime.UtcNow,
        };

        ActivityLog playbackLog = new()
        {
            Category = ActivityCategory.Playback,
            Type = "playback.started",
            UserId = userId,
            DeviceId = device,
            Time = DateTime.UtcNow,
        };

        context.ActivityLogs.AddRange(authLog, playbackLog);
        await context.SaveChangesAsync();

        int deleted = await repository.DeleteAsync(category: ActivityCategory.Auth, before: null);

        deleted.Should().Be(1);

        await using MediaContext verifyContext = new(_options);
        verifyContext
            .ActivityLogs.Should()
            .ContainSingle()
            .Which.Category.Should()
            .Be(ActivityCategory.Playback);
    }

    [Fact]
    public async Task DeleteAsync_removes_logs_before_date_and_excludes_after_date()
    {
        using MediaContext context = new(_options);
        Guid userId = Guid.NewGuid();
        Ulid device = Ulid.NewUlid();
        await SetupDeviceAndUser(context, userId, device);

        ActivityRepository repository = new(context);

        DateTime cutoff = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        ActivityLog beforeLog = new()
        {
            Category = ActivityCategory.Auth,
            Type = "auth.login",
            UserId = userId,
            DeviceId = device,
            Time = cutoff.AddMinutes(-5),
        };

        ActivityLog afterLog = new()
        {
            Category = ActivityCategory.Auth,
            Type = "auth.login",
            UserId = userId,
            DeviceId = device,
            Time = cutoff.AddMinutes(5),
        };

        context.ActivityLogs.AddRange(beforeLog, afterLog);
        await context.SaveChangesAsync();

        await PinCreatedAtAsync(context, beforeLog.Id, beforeLog.Time);
        await PinCreatedAtAsync(context, afterLog.Id, afterLog.Time);

        int deleted = await repository.DeleteAsync(category: null, before: cutoff);

        deleted.Should().Be(1);

        await using MediaContext verifyContext = new(_options);
        ActivityLog remaining = await verifyContext.ActivityLogs.SingleAsync();
        remaining.Id.Should().Be(afterLog.Id);
    }

    [Fact]
    public async Task DeleteAsync_removes_logs_matching_both_category_and_before_date()
    {
        using MediaContext context = new(_options);
        Guid userId = Guid.NewGuid();
        Ulid device = Ulid.NewUlid();
        await SetupDeviceAndUser(context, userId, device);

        ActivityRepository repository = new(context);

        DateTime cutoff = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        ActivityLog authBeforeCutoff = new()
        {
            Category = ActivityCategory.Auth,
            Type = "auth.login",
            UserId = userId,
            DeviceId = device,
            Time = cutoff.AddMinutes(-5),
        };

        ActivityLog authAfterCutoff = new()
        {
            Category = ActivityCategory.Auth,
            Type = "auth.login",
            UserId = userId,
            DeviceId = device,
            Time = cutoff.AddMinutes(5),
        };

        ActivityLog playbackBeforeCutoff = new()
        {
            Category = ActivityCategory.Playback,
            Type = "playback.started",
            UserId = userId,
            DeviceId = device,
            Time = cutoff.AddMinutes(-5),
        };

        context.ActivityLogs.AddRange(authBeforeCutoff, authAfterCutoff, playbackBeforeCutoff);
        await context.SaveChangesAsync();

        await PinCreatedAtAsync(context, authBeforeCutoff.Id, authBeforeCutoff.Time);
        await PinCreatedAtAsync(context, authAfterCutoff.Id, authAfterCutoff.Time);
        await PinCreatedAtAsync(context, playbackBeforeCutoff.Id, playbackBeforeCutoff.Time);

        int deleted = await repository.DeleteAsync(category: ActivityCategory.Auth, before: cutoff);

        deleted.Should().Be(1);

        await using MediaContext verifyContext = new(_options);
        List<ActivityLog> remaining = await verifyContext.ActivityLogs.ToListAsync();
        remaining.Should().HaveCount(2);
        remaining.Should().Contain(l => l.Id == authAfterCutoff.Id);
        remaining.Should().Contain(l => l.Id == playbackBeforeCutoff.Id);
    }
}
