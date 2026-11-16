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
namespace NoMercy.Api.Middleware;

public class HmacValidationOptions
{
    public string[] ProtectedPrefixes { get; set; } = ["/api/v1/distribution/", "/api/v1/worker/"];

    /// <summary>
    /// Largest request body the middleware reads to check a signature; anything
    /// bigger is refused with 413 before it is buffered. The body is read before
    /// the caller is known, so without a cap anyone sending dummy signature
    /// headers could make the server buffer up to Kestrel's own limit (100 GB).
    ///
    /// 1 MiB is far above the largest legitimate payload on these prefixes: the
    /// signed encode task posted to /api/v1/worker/, whose size is dominated by
    /// one ffmpeg argument list. That list has to fit a process command line
    /// (32 KiB on Windows), and register and heartbeat bodies are a few KiB at
    /// most. Progress pushes are exempt from the check and never read here.
    /// </summary>
    public long MaxBodyBytes { get; set; } = 1024 * 1024;
}
