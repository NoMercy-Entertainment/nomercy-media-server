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

namespace NoMercy.Tests.NmSystem.Extensions;

[Trait("Category", "Unit")]
public class ExceptionExtensionsTests
{
    private static HttpRequestException SslFailure(string innerMessage) =>
        new(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException(innerMessage)
        );

    [Fact]
    public void Unwrap_JoinsOuterAndInnerMessages()
    {
        HttpRequestException ex = SslFailure(
            "The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot"
        );

        string result = ex.Unwrap();

        result.Should().Contain("The SSL connection could not be established");
        result.Should().Contain("UntrustedRoot");
        result.Should().Contain(" -> ");
    }

    [Fact]
    public void Unwrap_SkipsDuplicateConsecutiveMessages()
    {
        InvalidOperationException ex = new(
            "same message",
            new InvalidOperationException("same message")
        );

        ex.Unwrap().Should().Be("same message");
    }

    [Fact]
    public void Unwrap_WithoutInnerException_ReturnsMessage()
    {
        new InvalidOperationException("plain failure").Unwrap().Should().Be("plain failure");
    }

    [Theory]
    [InlineData("A required certificate is not within its validity period")]
    [InlineData("errors in the certificate chain: NotTimeValid")]
    public void ConnectionAdvice_ClockProblem_PointsAtSystemTime(string innerMessage)
    {
        SslFailure(innerMessage).ConnectionAdvice().Should().Contain("date or time");
    }

    [Theory]
    [InlineData("errors in the certificate chain: UntrustedRoot")]
    [InlineData("errors in the certificate chain: PartialChain")]
    [InlineData("unable to get local issuer certificate")]
    public void ConnectionAdvice_TrustStoreProblem_PointsAtRootCertificates(string innerMessage)
    {
        SslFailure(innerMessage).ConnectionAdvice().Should().Contain("root certificates");
    }

    [Fact]
    public void ConnectionAdvice_NameMismatch_PointsAtInterception()
    {
        SslFailure("RemoteCertificateNameMismatch")
            .ConnectionAdvice()
            .Should()
            .Contain("intercepting");
    }

    [Fact]
    public void ConnectionAdvice_UnrecognizedTlsFailure_StillExplainsHandshake()
    {
        SslFailure("Authentication failed").ConnectionAdvice().Should().Contain("TLS handshake");
    }

    [Fact]
    public void ConnectionAdvice_DnsFailure_PointsAtDns()
    {
        HttpRequestException ex = new(
            "No such host is known.",
            new SocketException((int)SocketError.HostNotFound)
        );

        // SocketException carries its own OS message; the outer message holds the DNS text.
        ex.ConnectionAdvice().Should().Contain("DNS");
    }

    [Fact]
    public void ConnectionAdvice_Timeout_PointsAtFirewall()
    {
        TaskCanceledException ex = new("The request was canceled due to the configured timeout.");

        ex.ConnectionAdvice().Should().Contain("timed out");
    }

    [Fact]
    public void ConnectionAdvice_UnrelatedException_ReturnsNull()
    {
        new InvalidOperationException("Token exchange failed (400): invalid_grant")
            .ConnectionAdvice()
            .Should()
            .BeNull();
    }

    [Fact]
    public void DescribeConnectionFailure_AppendsAdviceToChain()
    {
        string result = SslFailure("errors in the certificate chain: UntrustedRoot")
            .DescribeConnectionFailure();

        result.Should().Contain("UntrustedRoot");
        result.Should().Contain("Likely cause:");
    }

    [Fact]
    public void DescribeConnectionFailure_WithoutAdvice_IsJustTheChain()
    {
        InvalidOperationException ex = new("Token exchange failed (400): invalid_grant");

        ex.DescribeConnectionFailure().Should().Be("Token exchange failed (400): invalid_grant");
    }
}
