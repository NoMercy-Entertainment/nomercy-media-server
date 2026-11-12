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

using System.Net.Sockets;
using System.Security.Authentication;

namespace NoMercy.NmSystem.Extensions;

public static class ExceptionExtensions
{
    /// <summary>
    /// Flattens an exception chain into one readable line. Top-level messages like
    /// "The SSL connection could not be established, see inner exception." are useless
    /// on their own — the inner exception holds the actual cause.
    /// </summary>
    public static string Unwrap(this Exception ex)
    {
        List<string> messages = [];

        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            string message = current.Message.Trim();
            if (message.Length == 0)
                continue;
            if (messages.Count == 0 || messages[^1] != message)
                messages.Add(message);
        }

        return string.Join(" -> ", messages);
    }

    /// <summary>
    /// Maps a failed outbound connection to a plain-language likely cause the user can
    /// act on, or null when the failure shape is not recognized.
    /// </summary>
    public static string? ConnectionAdvice(this Exception ex)
    {
        string chain = ex.Unwrap();
        bool isTls = HasInChain<AuthenticationException>(ex);

        if (
            ContainsAny(
                chain,
                "NotTimeValid",
                "not within its validity period",
                "certificate has expired",
                "not yet valid"
            )
        )
            return $"Likely cause: this machine's date or time is wrong. The clock reads {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC right now. Fix the system time (enable NTP time sync) and try again.";

        if (
            ContainsAny(
                chain,
                "UntrustedRoot",
                "PartialChain",
                "RemoteCertificateChainErrors",
                "unable to get local issuer certificate"
            )
        )
            return "Likely cause: this machine does not trust the certificate authority. Update the system root certificates (on Debian/Ubuntu: apt install ca-certificates, then update-ca-certificates) or install OS updates.";

        if (ContainsAny(chain, "RemoteCertificateNameMismatch"))
            return "Likely cause: something between this server and the internet is intercepting HTTPS traffic (a proxy, VPN, or DNS filter). The certificate presented does not belong to the expected host.";

        if (isTls)
            return "Likely cause: the TLS handshake failed. Check this machine's clock, its root certificates, and any proxy or firewall between it and the internet.";

        if (
            HasInChain<SocketException>(ex)
            && ContainsAny(
                chain,
                "No such host",
                "Name or service not known",
                "nodename nor servname"
            )
        )
            return "Likely cause: DNS lookup failed. This machine cannot resolve the host name. Check its network connection and DNS settings.";

        if (ContainsAny(chain, "Connection refused", "unreachable"))
            return "Likely cause: the network blocked the connection. Check this machine's internet access and any firewall rules for outbound HTTPS (port 443).";

        if (HasInChain<TaskCanceledException>(ex) || HasInChain<TimeoutException>(ex))
            return "Likely cause: the connection timed out. A firewall may be blocking outbound HTTPS (port 443), or the network is down.";

        return null;
    }

    /// <summary>
    /// Full user-facing description of a failed outbound connection: the flattened
    /// exception chain, plus a likely cause when the failure shape is recognized.
    /// </summary>
    public static string DescribeConnectionFailure(this Exception ex)
    {
        string chain = ex.Unwrap();
        string? advice = ex.ConnectionAdvice();
        return advice is null ? chain : $"{chain} {advice}";
    }

    /// <summary>Setup-page text for a failed connection: the likely cause or one plain
    /// sentence; the exception chain is log-only (#584).</summary>
    public static string DescribeConnectionFailureForUser(this Exception ex) =>
        ex.ConnectionAdvice()
        ?? "The request did not complete. Check this machine's network connection; the server log has the details.";

    private static bool HasInChain<T>(Exception ex)
        where T : Exception
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
            if (current is T)
                return true;
        return false;
    }

    private static bool ContainsAny(string haystack, params string[] needles) =>
        needles.Any(needle => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase));
}
