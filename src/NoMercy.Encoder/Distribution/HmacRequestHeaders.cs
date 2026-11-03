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

namespace NoMercy.Encoder.Distribution;

/// <summary>
/// Header names that carry a request signature between workers and the
/// coordinator. The signing side (<see cref="HmacSigningHandler"/>) and the
/// checking side (HmacValidationMiddleware) both read these, so the two
/// cannot drift apart.
/// </summary>
public static class HmacRequestHeaders
{
    /// <summary>Unix seconds (UTC) at which the request was signed.</summary>
    public const string Timestamp = "X-NoMercy-Timestamp";

    /// <summary>The signature produced by <see cref="HmacSigner.Sign"/>.</summary>
    public const string Signature = "X-NoMercy-Signature";

    /// <summary>Optional cluster token; when present the coordinator uses it as the secret.</summary>
    public const string WorkerToken = "X-NoMercy-WorkerToken";
}
