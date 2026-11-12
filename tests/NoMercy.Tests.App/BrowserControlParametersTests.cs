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

using NoMercy.App;
using Xunit;

namespace NoMercy.Tests.App;

public class BrowserControlParametersTests
{
    private const string DebugFlag = "--remote-debugging-port=9222";

    [Fact]
    public void Resolve_ReturnsNull_OnWindowsWithoutDebuggerOrOptIn()
    {
        string? result = BrowserControlParameters.Resolve(true, false, null);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("yes please")]
    public void Resolve_ReturnsNull_OnWindows_WhenOptInIsNotAnAcceptedValue(string optIn)
    {
        string? result = BrowserControlParameters.Resolve(true, false, optIn);

        result.Should().BeNull();
    }

    [Fact]
    public void Resolve_ReturnsFlag_OnWindowsWithDebuggerAttached()
    {
        string? result = BrowserControlParameters.Resolve(true, true, null);

        result.Should().Be(DebugFlag);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void Resolve_ReturnsFlag_OnWindowsWithOptIn(string optIn)
    {
        string? result = BrowserControlParameters.Resolve(true, false, optIn);

        result.Should().Be(DebugFlag);
    }

    [Fact]
    public void Resolve_ReturnsNull_OffWindows_EvenWithDebuggerAndOptIn()
    {
        string? result = BrowserControlParameters.Resolve(false, true, "1");

        result.Should().BeNull();
    }
}
