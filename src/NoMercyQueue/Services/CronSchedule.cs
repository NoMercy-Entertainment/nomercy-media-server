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

using System.Globalization;
using NCrontab;

namespace NoMercyQueue.Services;

/// <summary>
/// A five-field cron schedule as the queue runs it.
/// <para>
/// NCrontab does the work for everything it understands. The three day tokens
/// <c>CronExpressionBuilder</c> writes that NCrontab has no syntax for are
/// handled here: <c>L</c> in the day-of-month field (the last day of the
/// month), <c>dow#n</c> in the day-of-week field (the n-th such weekday of the
/// month) and <c>dowL</c> (the last such weekday of the month). Each is
/// rewritten to the plain field NCrontab can match plus a date filter, and
/// <see cref="GetNextOccurrence"/> walks NCrontab's candidates a day at a time
/// until one passes the filter.
/// </para>
/// <para>
/// Like Quartz, a day token cannot be combined with a restriction on the other
/// day field: cron's "either day field matches" rule has no clear meaning once
/// one side is "the last day", so <see cref="Parse"/> refuses it instead of
/// guessing.
/// </para>
/// </summary>
public sealed class CronSchedule
{
    private const int FieldCount = 5;
    private const int DayOfMonthField = 2;
    private const int DayOfWeekField = 4;
    private const char NthSeparator = '#';
    private const string Unrestricted = "*";

    // The last day of a month is always one of these four.
    private const string LastDaysOfMonth = "28-31";

    // A fifth weekday of one named month can be decades apart (the fifth
    // Monday of February needs a leap year that starts on a Monday), so the
    // search runs far enough to find any day the calendar can produce before
    // giving up on one it cannot.
    private const int MaxDaysSearched = 366 * 60;

    private static readonly char[] FieldSeparators = [' ', '\t'];

    private readonly CrontabSchedule _schedule;
    private readonly Func<DateTime, bool>? _dayFilter;

    private CronSchedule(CrontabSchedule schedule, Func<DateTime, bool>? dayFilter)
    {
        _schedule = schedule;
        _dayFilter = dayFilter;
    }

    /// <summary>
    /// Parses a five-field cron expression, including the <c>L</c>, <c>dow#n</c>
    /// and <c>dowL</c> day tokens.
    /// </summary>
    /// <exception cref="FormatException">
    /// A day token is malformed, or combined with a restriction on the other day field.
    /// </exception>
    /// <exception cref="CrontabException">NCrontab rejected the rest of the expression.</exception>
    public static CronSchedule Parse(string cronExpression)
    {
        string[] fields = cronExpression.Split(
            FieldSeparators,
            StringSplitOptions.RemoveEmptyEntries
        );

        // Anything that is not five fields is NCrontab's to accept or explain.
        if (fields.Length != FieldCount)
            return new(CrontabSchedule.Parse(cronExpression), null);

        string dayOfMonth = fields[DayOfMonthField];
        string dayOfWeek = fields[DayOfWeekField];
        Func<DateTime, bool>? dayFilter = null;

        if (IsLastDayOfMonthToken(dayOfMonth))
        {
            RequireUnrestricted(dayOfWeek, dayOfMonth, "day-of-week");
            fields[DayOfMonthField] = LastDaysOfMonth;
            dayFilter = IsLastDayOfMonth;
        }
        else if (TryParseNthWeekday(dayOfWeek, out int nthWeekday, out int nth))
        {
            RequireUnrestricted(dayOfMonth, dayOfWeek, "day-of-month");
            fields[DayOfWeekField] = nthWeekday.ToString(CultureInfo.InvariantCulture);
            dayFilter = date => IsInNthWeekOfMonth(date, nth);
        }
        else if (TryParseLastWeekday(dayOfWeek, out int lastWeekday))
        {
            RequireUnrestricted(dayOfMonth, dayOfWeek, "day-of-month");
            fields[DayOfWeekField] = lastWeekday.ToString(CultureInfo.InvariantCulture);
            dayFilter = IsInLastWeekOfMonth;
        }

        return new(CrontabSchedule.Parse(string.Join(' ', fields)), dayFilter);
    }

    /// <summary>
    /// The first occurrence strictly after <paramref name="baseTime"/>, or
    /// <see cref="DateTime.MaxValue"/> when there is none, which is also what
    /// NCrontab answers for a schedule that never fires.
    /// </summary>
    public DateTime GetNextOccurrence(DateTime baseTime)
    {
        DateTime candidate = _schedule.GetNextOccurrence(baseTime);

        if (_dayFilter is null)
            return candidate;

        for (int daysSearched = 0; daysSearched < MaxDaysSearched; daysSearched++)
        {
            if (candidate >= DateTime.MaxValue.Date)
                return DateTime.MaxValue;

            if (_dayFilter(candidate))
                return candidate;

            // The filter reads only the date, so once a day fails every later
            // slot on it fails too: resume from that day's last minute.
            candidate = _schedule.GetNextOccurrence(candidate.Date.AddDays(1).AddMinutes(-1));
        }

        return DateTime.MaxValue;
    }

    private static bool IsLastDayOfMonth(DateTime date) =>
        date.Day == DateTime.DaysInMonth(date.Year, date.Month);

    private static bool IsInNthWeekOfMonth(DateTime date, int nth) => (date.Day - 1) / 7 + 1 == nth;

    private static bool IsInLastWeekOfMonth(DateTime date) =>
        date.Day + 7 > DateTime.DaysInMonth(date.Year, date.Month);

    private static bool IsLastDayOfMonthToken(string field) =>
        field.Length == 1 && IsLastToken(field[0]);

    private static bool IsLastToken(char character) => character is 'L' or 'l';

    private static bool TryParseNthWeekday(string field, out int weekday, out int nth)
    {
        weekday = 0;
        nth = 0;

        int separator = field.IndexOf(NthSeparator);
        if (separator < 0)
            return false;

        bool wellFormed =
            TryParseDigits(field[..separator], out weekday)
            && TryParseDigits(field[(separator + 1)..], out nth)
            && nth is >= 1 and <= 5;

        if (!wellFormed)
            throw new FormatException(
                $"'{field}' is not a valid day-of-week token: '#' takes the form 'weekday#n' with n between 1 and 5, as in '1#2' for the second Monday."
            );

        return true;
    }

    private static bool TryParseLastWeekday(string field, out int weekday)
    {
        weekday = 0;

        if (field.Length < 2 || !IsLastToken(field[^1]))
            return false;

        if (!TryParseDigits(field[..^1], out weekday))
            throw new FormatException(
                $"'{field}' is not a valid day-of-week token: 'L' takes the form 'weekdayL', as in '5L' for the last Friday."
            );

        return true;
    }

    private static bool TryParseDigits(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static void RequireUnrestricted(string otherField, string token, string otherFieldName)
    {
        if (otherField == Unrestricted)
            return;

        throw new FormatException(
            $"'{token}' cannot be combined with a {otherFieldName} restriction ('{otherField}'); set {otherFieldName} to '*'."
        );
    }
}
