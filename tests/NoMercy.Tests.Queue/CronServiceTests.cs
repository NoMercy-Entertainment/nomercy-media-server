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
using NCrontab;
using NoMercyQueue.Core;
using NoMercyQueue.Services;
using Xunit;

namespace NoMercy.Tests.Queue;

/// <summary>
/// <see cref="CronService.ShouldRun"/> is the public decision function a
/// caller uses to ask "has this cron job's next occurrence, computed from its
/// last run, already arrived?" — a plugin-facing alternative to CronWorker's
/// own internal NextRun bookkeeping loop.
/// <para>
/// <see cref="CronService.GetNextOccurrence"/> is also the one parse path every
/// schedule string goes through, so the three day tokens
/// <see cref="CronExpressionBuilder"/> can write that NCrontab itself has no
/// syntax for (<c>L</c>, <c>dow#n</c>, <c>dowL</c>) are pinned here against
/// real calendar dates (issue #399).
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class CronServiceTests
{
    [Fact]
    public void ShouldRun_CurrentTimeAtOrAfterNextOccurrence_ReturnsTrue()
    {
        DateTime lastRun = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime currentTime = new(2025, 1, 2, 0, 0, 1, DateTimeKind.Utc);

        CronService.ShouldRun("0 0 * * *", lastRun, currentTime).Should().BeTrue();
    }

    [Fact]
    public void ShouldRun_CurrentTimeBeforeNextOccurrence_ReturnsFalse()
    {
        DateTime lastRun = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime currentTime = new(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        CronService.ShouldRun("0 0 * * *", lastRun, currentTime).Should().BeFalse();
    }

    [Fact]
    public void GetNextOccurrence_DailyExpression_ReturnsNextMidnight()
    {
        DateTime baseTime = new(2025, 6, 10, 14, 0, 0, DateTimeKind.Utc);

        DateTime next = CronService.GetNextOccurrence("0 0 * * *", baseTime);

        next.Should().Be(new DateTime(2025, 6, 11, 0, 0, 0, DateTimeKind.Unspecified));
    }

    public static TheoryData<string> BuilderDayTokenExpressions =>
        new()
        {
            new CronExpressionBuilder().LastDayOfMonth().Build(),
            new CronExpressionBuilder().OnNthDayOfWeek(2, DayOfWeek.Monday).Build(),
            new CronExpressionBuilder().LastDayOfWeek(DayOfWeek.Friday).Build(),
        };

    [Theory]
    [MemberData(nameof(BuilderDayTokenExpressions))]
    public void GetNextOccurrence_EveryBuilderDayToken_IsAcceptedByTheScheduler(
        string cronExpression
    )
    {
        DateTime baseTime = new(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        Action act = () => CronService.GetNextOccurrence(cronExpression, baseTime);

        act.Should().NotThrow("the builder wrote '{0}' for the scheduler to run", cronExpression);
    }

    [Fact]
    public void GetNextOccurrence_LastDayOfMonth_LandsOnTheLastDayOfEachMonth()
    {
        string cronExpression = new CronExpressionBuilder().LastDayOfMonth();

        DateTime january = CronService.GetNextOccurrence(cronExpression, new DateTime(2025, 1, 15));
        DateTime february = CronService.GetNextOccurrence(cronExpression, january);
        DateTime leapFebruary = CronService.GetNextOccurrence(
            cronExpression,
            new DateTime(2024, 2, 1)
        );

        january.Should().Be(new DateTime(2025, 1, 31));
        february.Should().Be(new DateTime(2025, 2, 28));
        leapFebruary.Should().Be(new DateTime(2024, 2, 29));
    }

    [Fact]
    public void GetNextOccurrence_LastDayOfMonthWithSeveralSlotsADay_KeepsEverySlotOfThatDay()
    {
        string cronExpression = new CronExpressionBuilder().LastDayOfMonth().EveryMinutes(30);

        DateTime secondSlot = CronService.GetNextOccurrence(
            cronExpression,
            new DateTime(2025, 1, 31, 0, 0, 0)
        );
        DateTime nextMonth = CronService.GetNextOccurrence(cronExpression, secondSlot);

        secondSlot.Should().Be(new DateTime(2025, 1, 31, 0, 30, 0));
        nextMonth.Should().Be(new DateTime(2025, 2, 28, 0, 0, 0));
    }

    [Fact]
    public void GetNextOccurrence_NthWeekday_LandsOnThatWeekdayOfEachMonth()
    {
        string cronExpression = new CronExpressionBuilder().OnNthDayOfWeek(2, DayOfWeek.Friday);

        DateTime september = CronService.GetNextOccurrence(
            cronExpression,
            new DateTime(2025, 9, 1)
        );
        DateTime october = CronService.GetNextOccurrence(cronExpression, september);

        september
            .Should()
            .Be(new DateTime(2025, 9, 12), "Fridays in September 2025 are 5, 12, 19, 26");
        october
            .Should()
            .Be(new DateTime(2025, 10, 10), "Fridays in October 2025 are 3, 10, 17, 24, 31");
    }

    [Fact]
    public void GetNextOccurrence_FifthWeekday_SkipsMonthsThatHaveNoFifth()
    {
        string cronExpression = new CronExpressionBuilder().OnNthDayOfWeek(5, DayOfWeek.Monday);

        DateTime september = CronService.GetNextOccurrence(
            cronExpression,
            new DateTime(2025, 9, 1)
        );
        DateTime december = CronService.GetNextOccurrence(cronExpression, september);

        september.Should().Be(new DateTime(2025, 9, 29));
        december
            .Should()
            .Be(new DateTime(2025, 12, 29), "October and November 2025 have only four Mondays");
    }

    [Fact]
    public void GetNextOccurrence_LastWeekday_LandsOnTheLastSuchWeekdayOfEachMonth()
    {
        string cronExpression = new CronExpressionBuilder().LastDayOfWeek(DayOfWeek.Friday);

        DateTime september = CronService.GetNextOccurrence(
            cronExpression,
            new DateTime(2025, 9, 1)
        );
        DateTime october = CronService.GetNextOccurrence(cronExpression, september);

        september.Should().Be(new DateTime(2025, 9, 26));
        october.Should().Be(new DateTime(2025, 10, 31));
    }

    [Theory]
    [InlineData("0 0 L * 1", "L")]
    [InlineData("0 0 15 * 1#2", "1#2")]
    [InlineData("0 0 15 * 5L", "5L")]
    public void GetNextOccurrence_DayTokenCombinedWithTheOtherDayField_IsRefusedNamingTheToken(
        string cronExpression,
        string token
    )
    {
        Action act = () => CronService.GetNextOccurrence(cronExpression, new DateTime(2025, 1, 1));

        act.Should().Throw<FormatException>().WithMessage($"*'{token}'*");
    }

    [Theory]
    [InlineData("0 0 * * 1#0")]
    [InlineData("0 0 * * 1#6")]
    [InlineData("0 0 * * Mon#2")]
    public void GetNextOccurrence_MalformedNthWeekdayToken_IsRefusedNamingTheToken(
        string cronExpression
    )
    {
        Action act = () => CronService.GetNextOccurrence(cronExpression, new DateTime(2025, 1, 1));

        act.Should().Throw<FormatException>().WithMessage("*'#'*");
    }

    [Fact]
    public void GetNextOccurrence_PlainInvalidExpression_StillSurfacesTheSchedulerError()
    {
        Action act = () => CronService.GetNextOccurrence("0 0 32 * *", new DateTime(2025, 1, 1));

        act.Should().Throw<CrontabException>();
    }
}
