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

using NoMercy.Setup.Server;

namespace NoMercy.Tests.Setup.Server;

[Trait("Category", "Unit")]
public sealed class ApiKeyStoreQuoteTests
{
    [Fact]
    public void EveryQuoteUsesApostrophesInsteadOfDoubleQuotes()
    {
        HashSet<string> quotes = [];

        for (int attempt = 0; attempt < 2048 && quotes.Count < 32; attempt++)
            quotes.Add(new ApiKeyStore().Quote);

        Assert.Equal(32, quotes.Count);
        Assert.All(quotes, quote => Assert.DoesNotContain('"', quote));
    }
}
