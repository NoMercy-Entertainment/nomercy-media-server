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

namespace NoMercy.Tests.NmSystem;

[Trait("Category", "Unit")]
public class CollectionTitleFormatterTests
{
    [Fact]
    public void StripCollectionSuffix_TrailingWord_IsRemoved()
    {
        string result = CollectionTitleFormatter.StripCollectionSuffix("Accident Man Collection");

        result.Should().Be("Accident Man");
    }

    [Fact]
    public void StripCollectionSuffix_WordAlone_IsNotReducedToEmpty()
    {
        string result = CollectionTitleFormatter.StripCollectionSuffix("Collection");

        result.Should().Be("Collection");
    }

    [Fact]
    public void StripCollectionSuffix_WordInMiddle_IsUntouched()
    {
        string result = CollectionTitleFormatter.StripCollectionSuffix(
            "Collection of Short Stories"
        );

        result.Should().Be("Collection of Short Stories");
    }

    [Fact]
    public void StripCollectionSuffix_CaseInsensitiveAndWhitespace_IsRemoved()
    {
        string result = CollectionTitleFormatter.StripCollectionSuffix(
            "101 Dalmatians (Animated) collection"
        );

        result.Should().Be("101 Dalmatians (Animated)");
    }

    [Fact]
    public void ResolveDisplayTitle_UsersLanguageWinsOverEnglish()
    {
        string result = CollectionTitleFormatter.ResolveDisplayTitle(
            "基础标题",
            "Franquicia Accidente Hombre",
            "Accident Man Collection"
        );

        result.Should().Be("Franquicia Accidente Hombre");
    }

    [Fact]
    public void ResolveDisplayTitle_FallsBackToEnglish_WhenNoLanguageTranslation()
    {
        string result = CollectionTitleFormatter.ResolveDisplayTitle(
            "猫和老鼠：黄金时代合集",
            null,
            "Tom and Jerry: The Golden Era Anthology Collection"
        );

        result.Should().Be("Tom and Jerry: The Golden Era Anthology");
    }

    [Fact]
    public void ResolveDisplayTitle_FallsBackToBaseTitle_WhenNoTranslationsExist()
    {
        string result = CollectionTitleFormatter.ResolveDisplayTitle(
            "猫和老鼠：黄金时代合集",
            null,
            null
        );

        result.Should().Be("猫和老鼠：黄金时代合集");
    }
}
