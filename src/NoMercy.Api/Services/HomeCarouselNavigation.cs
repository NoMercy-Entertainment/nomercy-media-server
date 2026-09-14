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
namespace NoMercy.Api.Services;

/// <summary>
/// Previous and next carousel ids on the home page. The carousels form one chain;
/// "continue watching", when shown, joins its ends into a ring.
/// </summary>
public static class HomeCarouselNavigation
{
    public static (string? Previous, string? Next) ForCarousel(
        IReadOnlyList<string> carouselIds,
        int index,
        string? continueId
    )
    {
        string? previous = index == 0 ? continueId : carouselIds[index - 1];
        string? next = index == carouselIds.Count - 1 ? continueId : carouselIds[index + 1];
        return (previous, next);
    }

    public static (string? Previous, string? Next) ForContinue(IReadOnlyList<string> carouselIds) =>
        carouselIds.Count == 0 ? (null, null) : (carouselIds[^1], carouselIds[0]);
}
