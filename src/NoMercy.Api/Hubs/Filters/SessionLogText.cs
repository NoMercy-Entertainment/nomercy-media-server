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

using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;

namespace NoMercy.Api.Hubs.Filters;

internal static partial class SessionLogText
{
    public const string Unknown = "-";

    [GeneratedRegex(
        @"^[A-Za-z0-9_\-.:]{1,64}\z",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex SafeIdentifier();

    /// <summary>
    /// Returns the text only when it is a short identifier. A token, a header value or a
    /// name has spaces, dots between long segments or other characters and comes back null.
    /// </summary>
    public static string? SafeIdentifierOrNull(string? text) =>
        text is not null
        && !text.StartsWith("eyJ", StringComparison.Ordinal)
        && SafeIdentifier().IsMatch(text)
            ? text
            : null;

    public static string DeviceOf(HubCallerContext context) =>
        SafeIdentifierOrNull(context.GetHttpContext()?.Request.Query["client_id"].ToString())
        ?? Unknown;
}
