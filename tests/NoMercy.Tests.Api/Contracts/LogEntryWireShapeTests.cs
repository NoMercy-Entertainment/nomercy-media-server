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
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using NoMercy.NmSystem.Dto;
using Serilog.Events;
using Xunit;

namespace NoMercy.Tests.Api.Contracts;

/// <summary>
/// What one log line costs on the wire.
///
/// A log entry leaves the server three ways: a list endpoint, a server-sent
/// event stream, and a websocket broadcast. The stream is live and busy, so a
/// property carried twice is paid for on every line.
/// </summary>
[Trait("Category", "Unit")]
public class LogEntryWireShapeTests
{
    private static JObject Serialize(LogEntry entry)
    {
        JsonSerializerSettings settings = new()
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        };
        settings.Converters.Add(new StringEnumConverter());
        return JObject.Parse(JsonConvert.SerializeObject(entry, settings));
    }

    private static LogEntry Entry()
    {
        return new()
        {
            Type = "info",
            Color = "blue",
            ThreadId = 7,
            Time = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc),
            Message = "Plugin loaded: Internet Radio 2.0.0",
            LogLevel = LogEventLevel.Information,
        };
    }

    [Fact]
    public void A_log_line_carries_its_message_once()
    {
        JObject wire = Serialize(Entry());

        wire["message"]!.Value<string>().Should().Be("Plugin loaded: Internet Radio 2.0.0");
        wire.Properties()
            .Select(property => property.Name)
            .Should()
            .NotContain("LogMessage", "the message is already on the line as message");
    }

    [Fact]
    public void A_log_line_carries_its_level_once()
    {
        JObject wire = Serialize(Entry());

        wire["level"]!.Value<string>().Should().Be("Information");
        wire.Properties()
            .Select(property => property.Name)
            .Should()
            .NotContain("LogLevel", "the level is already on the line as level");
    }

    /// <summary>
    /// The names a dashboard reads, in the casing it reads them in. Newtonsoft
    /// writes the property name as declared unless an attribute it can see says
    /// otherwise, so a System.Text.Json name here would be a silently wrong key.
    /// </summary>
    [Fact]
    public void A_log_line_uses_the_keys_the_dashboard_reads()
    {
        JObject wire = Serialize(Entry());

        wire.Properties()
            .Select(property => property.Name)
            .Should()
            .Contain(["type", "color", "threadId", "time", "message", "level"]);
    }
}
