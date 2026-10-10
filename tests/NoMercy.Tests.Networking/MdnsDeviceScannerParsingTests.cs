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

using System.Net;
using Makaretu.Dns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Database;
using NoMercy.Networking.Discovery;
using Xunit;
using Message = Makaretu.Dns.Message;

namespace NoMercy.Tests.Networking;

/// <summary>
/// REQUIREMENT: ExtractFingerprint must find the device's fp=... TXT record
/// (case-insensitive prefix) among possibly many other TXT strings, and
/// ExtractEndpoint must pair the SRV record's port with the A record's
/// address — both must fail soft (return null / (null,null)) when the
/// expected record is absent rather than throwing, since a malformed mDNS
/// response from a third-party device on the LAN must never crash the
/// scanner. These operate on real Makaretu.Dns message objects — no
/// multicast socket is involved.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MdnsDeviceScannerParsingTests
{
    private static TXTRecord Txt(params string[] strings) => new() { Strings = [.. strings] };

    [Fact]
    public void ExtractFingerprint_FindsFpPrefixedString()
    {
        Message msg = new();
        msg.AdditionalRecords.Add(Txt(["v=1", "fp=abc123", "other=value"]));

        string? fingerprint = MdnsDeviceScanner.ExtractFingerprint(msg);

        Assert.Equal("abc123", fingerprint);
    }

    [Fact]
    public void ExtractFingerprint_PrefixMatchIsCaseInsensitive()
    {
        Message msg = new();
        msg.AdditionalRecords.Add(Txt("FP=xyz789"));

        string? fingerprint = MdnsDeviceScanner.ExtractFingerprint(msg);

        Assert.Equal("xyz789", fingerprint);
    }

    [Fact]
    public void ExtractFingerprint_NoTxtRecords_ReturnsNull()
    {
        Message msg = new();

        string? fingerprint = MdnsDeviceScanner.ExtractFingerprint(msg);

        Assert.Null(fingerprint);
    }

    [Fact]
    public void ExtractFingerprint_TxtRecordWithoutFpPrefix_ReturnsNull()
    {
        Message msg = new();
        msg.AdditionalRecords.Add(Txt(["v=1", "name=Living Room TV"]));

        string? fingerprint = MdnsDeviceScanner.ExtractFingerprint(msg);

        Assert.Null(fingerprint);
    }

    [Fact]
    public void ExtractFingerprint_MultipleTxtRecords_FindsFirstMatch()
    {
        Message msg = new();
        msg.AdditionalRecords.Add(Txt("v=1"));
        msg.AdditionalRecords.Add(Txt("fp=second-record"));

        string? fingerprint = MdnsDeviceScanner.ExtractFingerprint(msg);

        Assert.Equal("second-record", fingerprint);
    }

    [Fact]
    public void ExtractFingerprint_EmptyFpValue_ReturnsEmptyString()
    {
        Message msg = new();
        msg.AdditionalRecords.Add(Txt("fp="));

        string? fingerprint = MdnsDeviceScanner.ExtractFingerprint(msg);

        Assert.Equal(string.Empty, fingerprint);
    }

    [Fact]
    public void ExtractEndpoint_WithSrvAndA_ReturnsIpAndPort()
    {
        Message msg = new();
        msg.AdditionalRecords.Add(new SRVRecord { Port = 8009, Target = new("device.local") });
        msg.AdditionalRecords.Add(new ARecord { Address = IPAddress.Parse("192.168.1.42") });

        (string? ip, int? port) = MdnsDeviceScanner.ExtractEndpoint(msg);

        Assert.Equal("192.168.1.42", ip);
        Assert.Equal(8009, port);
    }

    [Fact]
    public void ExtractEndpoint_NoSrvRecord_ReturnsNullTuple()
    {
        Message msg = new();
        msg.AdditionalRecords.Add(new ARecord { Address = IPAddress.Parse("192.168.1.42") });

        (string? ip, int? port) = MdnsDeviceScanner.ExtractEndpoint(msg);

        Assert.Null(ip);
        Assert.Null(port);
    }

    [Fact]
    public void ExtractEndpoint_SrvWithoutARecord_ReturnsNullIpButRealPort()
    {
        Message msg = new();
        msg.AdditionalRecords.Add(new SRVRecord { Port = 8009, Target = new("device.local") });

        (string? ip, int? port) = MdnsDeviceScanner.ExtractEndpoint(msg);

        Assert.Null(ip);
        Assert.Equal(8009, port);
    }

    [Fact]
    public void ExtractEndpoint_NoRecordsAtAll_ReturnsNullTuple()
    {
        Message msg = new();

        (string? ip, int? port) = MdnsDeviceScanner.ExtractEndpoint(msg);

        Assert.Null(ip);
        Assert.Null(port);
    }

    // -- Lifecycle: Start()/Dispose() idempotency and disposal safety. The
    // real multicast join and mDNS wire receive (OnInstanceDiscovered firing
    // from an actual network packet, and the DB upsert it triggers) require a
    // live LAN multicast group and are itemized as not unit-testable —
    // see the coverage report.

    [Fact]
    public void Dispose_WithoutStart_DoesNotThrow()
    {
        MdnsDeviceScanner scanner = new(
            new ThrowingDbContextFactory(),
            NullLogger<MdnsDeviceScanner>.Instance,
            multicast: new NonBindingMulticastTransport()
        );

        Exception? ex = Record.Exception(scanner.Dispose);

        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        MdnsDeviceScanner scanner = new(
            new ThrowingDbContextFactory(),
            NullLogger<MdnsDeviceScanner>.Instance,
            multicast: new NonBindingMulticastTransport()
        );

        Exception? ex = Record.Exception(() =>
        {
            scanner.Dispose();
            scanner.Dispose();
        });

        Assert.Null(ex);
    }

    private sealed class ThrowingDbContextFactory : IDbContextFactory<MediaContext>
    {
        public MediaContext CreateDbContext() => throw new NotSupportedException();
    }
}
