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

using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Database.Models.Libraries;
using Xunit;

namespace NoMercy.Tests.Api.Dtos;

/// <summary>
/// The library list is what the dashboard edits a library from, so every
/// stored setting has to come back as stored. chapterImages and updated_at
/// were declared on the DTO but never copied, so the chapter images switch
/// always showed as off no matter what was saved.
/// </summary>
public class LibrariesResponseItemDtoTests
{
    private static Library BuildLibrary(bool chapterImages, DateTime? updatedAt = null)
    {
        return new()
        {
            Id = Ulid.NewUlid(),
            Title = "Movies",
            Type = "movie",
            ChapterImages = chapterImages,
            CreatedAt = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = updatedAt ?? new(2024, 6, 1, 12, 30, 0, DateTimeKind.Utc),
        };
    }

    [Fact]
    public void A_library_with_chapter_images_on_reports_1()
    {
        LibrariesResponseItemDto dto = new(BuildLibrary(chapterImages: true));

        dto.ChapterImages.Should().Be(1, "the saved setting is on");
    }

    [Fact]
    public void A_library_with_chapter_images_off_reports_0()
    {
        LibrariesResponseItemDto dto = new(BuildLibrary(chapterImages: false));

        dto.ChapterImages.Should().Be(0);
    }

    [Fact]
    public void The_updated_at_stamp_is_the_library_stamp()
    {
        DateTime updatedAt = new(2025, 3, 15, 8, 45, 0, DateTimeKind.Utc);

        LibrariesResponseItemDto dto = new(BuildLibrary(chapterImages: true, updatedAt));

        dto.UpdatedAt.Should().Be(updatedAt);
    }
}
