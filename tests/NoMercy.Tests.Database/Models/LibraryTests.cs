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
using NoMercy.Database.Models.Libraries;
using Xunit;

namespace NoMercy.Tests.Database.Models;

[Trait("Category", "Unit")]
public class LibraryTests
{
    [Fact]
    public void AutoConfirmDiscMatches_DefaultsToFalse()
    {
        Library library = new();

        library.AutoConfirmDiscMatches.Should().BeFalse();
    }

    [Fact]
    public void AutoConfirmDiscMatches_CanBeSetTrue()
    {
        Library library = new() { AutoConfirmDiscMatches = true };

        library.AutoConfirmDiscMatches.Should().BeTrue();
    }

    /// <summary>
    /// Audio analysis is part of every music import, not a setting a user has
    /// to go and find, so a library that says nothing about it wants it.
    /// </summary>
    [Fact]
    public void AnalyzeAudio_DefaultsToOn()
    {
        Library library = new();

        library.AnalyzeAudio.Should().BeTrue();
    }

    [Fact]
    public void AnalyzeAudio_CanBeSetOff()
    {
        Library library = new() { AnalyzeAudio = false };

        library.AnalyzeAudio.Should().BeFalse();
    }
}
