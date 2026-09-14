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

using System.Diagnostics.CodeAnalysis;

namespace NoMercy.NmSystem.Extensions;

public static class NullableExtensions
{
    /// <summary>
    /// The string, or <paramref name="fallback"/> when it is null or empty. A translated
    /// title or overview falls back to the original this way.
    /// </summary>
    [return: NotNullIfNotNull(nameof(fallback))]
    public static string? OrWhenEmpty(this string? value, string? fallback) =>
        string.IsNullOrEmpty(value) ? fallback : value;

    /// <summary>
    /// Returns the string if not null, otherwise returns an empty string.
    /// Inspired by Kotlin's String?.orEmpty()
    /// </summary>
    public static string OrEmpty(this string? value) => value ?? string.Empty;

    /// <summary>
    /// Returns the array if not null, otherwise returns an empty array.
    /// </summary>
    public static T[] OrEmpty<T>(this T[]? value) => value ?? [];

    /// <summary>
    /// Returns the list if not null, otherwise returns an empty list.
    /// </summary>
    public static List<T> OrEmpty<T>(this List<T>? value) => value ?? [];

    /// <summary>
    /// Returns the enumerable if not null, otherwise returns an empty enumerable.
    /// </summary>
    public static IEnumerable<T> OrEmpty<T>(this IEnumerable<T>? value) => value ?? [];

    /// <summary>
    /// Returns null if the string is null, empty, or whitespace; otherwise returns the string.
    /// Inspired by Kotlin's String?.takeIf { it.isNotBlank() }
    /// </summary>
    public static string? OrNull(this string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
