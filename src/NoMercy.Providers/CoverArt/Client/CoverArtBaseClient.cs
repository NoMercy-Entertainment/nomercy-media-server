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

using NoMercy.NmSystem.SystemCalls;
using NoMercy.Providers.Abstractions;
using NoMercy.Providers.Helpers;
using Serilog.Events;

namespace NoMercy.Providers.CoverArt.Client;

public class CoverArtBaseClient : ExternalApiClient
{
    protected CoverArtBaseClient() { }

    protected CoverArtBaseClient(Guid id)
        : base(id) { }

    protected override string HttpClientName => HttpClientNames.CoverArt;
    protected override Uri BaseUrl => new("https://coverartarchive.org/");

    protected override void LogRequest(string url) => Logger.CoverArt(url, LogEventLevel.Verbose);
}
