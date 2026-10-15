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
using Microsoft.Extensions.Logging;
using NoMercy.PluginSdk.Ipc;
using Xunit;

namespace NoMercy.Tests.PluginHost;

/// <summary>
/// The format a plugin host writes its log in and the server reads it back
/// with. One entry must stay one line, whatever is in it, and anything that
/// is not a whole entry must be refused so the server logs it as text.
/// </summary>
[Trait("Category", "Unit")]
public class PluginHostLogLineTests
{
    [Fact]
    public void AnEntry_ReadsBackAsItWasWritten()
    {
        PluginHostLogLine written = new(LogLevel.Warning, "Tracker unreachable", "trace");

        PluginHostLogLine.TryParse(written.Format(), out PluginHostLogLine? read).Should().BeTrue();

        read.Should().Be(written);
    }

    /// <summary>
    /// The server splits the pipe on line breaks. A message or stack trace
    /// over several lines must not split the entry.
    /// </summary>
    [Fact]
    public void AnEntryOverSeveralLines_IsWrittenAsOneLine()
    {
        string line = new PluginHostLogLine(
            LogLevel.Error,
            "first\nsecond\r\nthird",
            "at A()\n   at B()"
        ).Format();

        line.Should().NotContain("\n").And.NotContain("\r");
        PluginHostLogLine.TryParse(line, out PluginHostLogLine? read).Should().BeTrue();
        read!.Message.Should().Be("first\nsecond\r\nthird");
        read.Exception.Should().Be("at A()\n   at B()");
    }

    /// <summary>
    /// A console on Windows writes in the machine's code page. A line that is
    /// plain ASCII arrives intact in any of them.
    /// </summary>
    [Fact]
    public void ANameInAnyScript_IsWrittenAsPlainAscii_AndReadsBackTheSame()
    {
        string message = "Café → 東京 ✓";

        string line = new PluginHostLogLine(LogLevel.Information, message, null).Format();

        line.All(character => character < 128).Should().BeTrue();
        PluginHostLogLine.TryParse(line, out PluginHostLogLine? read).Should().BeTrue();
        read!.Message.Should().Be(message);
    }

    [Theory]
    [InlineData("a plugin printing to the console")]
    [InlineData("")]
    [InlineData("nomercy:log")]
    [InlineData("nomercy:log {not json")]
    [InlineData("nomercy:log {\"level\":\"Information\"}")]
    [InlineData("nomercy:log {\"level\":\"Loud\",\"message\":\"x\"}")]
    [InlineData("nomercy:log {\"level\":\"None\",\"message\":\"x\"}")]
    [InlineData("nomercy:log {\"level\":99,\"message\":\"x\"}")]
    [InlineData("nomercy:log null")]
    public void ALineThatIsNotAWholeEntry_IsRefused(string line)
    {
        PluginHostLogLine.TryParse(line, out PluginHostLogLine? entry).Should().BeFalse();
        entry.Should().BeNull();
    }
}
