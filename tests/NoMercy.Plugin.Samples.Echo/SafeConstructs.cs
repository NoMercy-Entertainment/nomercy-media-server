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

using System.Collections.Immutable;

namespace NoMercy.Plugin.Samples.Echo;

/// <summary>
/// Safe C# the compiler lowers to helpers that look like escape hatches
/// (inline-array spans, localloc, DefaultMemberAttribute). A plugin written
/// this way must pass the code scan.
/// </summary>
public sealed class SafeConstructs
{
    // A delegate's methods carry CodeType Runtime; the scan must not read
    // them as native code.
    public delegate int Pick(int index);

    private readonly string[] _names = ["a", "b", "c"];

    public string this[int index] => _names[index];

    public string Join(string a, string b, string c) => string.Join(", ", a, b, c);

    public string Combine(string a, string b, string c, string d) => Path.Combine(a, b, c, d);

    public int Sum()
    {
        ReadOnlySpan<int> values = [1, 2, 3];
        Span<byte> scratch = stackalloc byte[16];
        scratch[0] = 1;
        int total = scratch[0];
        foreach (int value in values)
            total += value;
        return total;
    }

    public async Task<int> Later()
    {
        await Task.Yield();
        return _names.Length;
    }

    // A List<T> collection expression with elements and a spread lowers to
    // CollectionsMarshal.SetCount and CollectionsMarshal.AsSpan; an
    // ImmutableArray<T> one lowers to
    // ImmutableCollectionsMarshal.AsImmutableArray. Fillz's radio plugin was
    // refused on the first shape without naming either type.
    public int Merge(int a, int b, int[] rest)
    {
        List<int> merged = [a, b, .. rest];
        ImmutableArray<int> frozen = [.. merged];
        return merged.Count + frozen.Length;
    }
}
