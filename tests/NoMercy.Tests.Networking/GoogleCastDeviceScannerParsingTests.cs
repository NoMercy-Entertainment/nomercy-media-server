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

using Makaretu.Dns;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Networking.Discovery;
using Xunit;
using Message = Makaretu.Dns.Message;

namespace NoMercy.Tests.Networking;

/// <summary>
/// REQUIREMENT: ExtractCastInfo must find Google Cast's standard `id=`, `fn=`,
/// and `md=` TXT keys (case-insensitive prefix, first match wins per key)
/// among possibly many other TXT strings (`ca=`, `st=`, `rs=`, etc. are
/// present on a real device and must be ignored rather than crash parsing),
/// and must fail soft — null fields, never a throw — when a key is absent,
/// since a malformed response from a third-party device on the LAN must never
/// crash the scanner. SRV/A endpoint extraction is intentionally NOT
/// retested here: GoogleCastDeviceScanner reuses
/// MdnsDeviceScanner.ExtractEndpoint directly, already covered by
/// MdnsDeviceScannerParsingTests. IsReachable's TTL/match behavior needs a
/// live-seen entry, which only OnInstanceDiscovered (private, fired from a
/// real multicast packet) can populate — so only its documented empty/null
/// contract is asserted here, same boundary MdnsDeviceScannerParsingTests
/// draws around DB-upsert behavior.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GoogleCastDeviceScannerParsingTests
{
    private static TXTRecord Txt(params string[] strings) => new() { Strings = [.. strings] };

    [Fact]
    public void ExtractCastInfo_FindsIdFnMdKeys()
    {
        Message msg = new();
        msg.AdditionalRecords.Add(
            Txt(["id=a1b2c3d4e5f6", "fn=Living Room TV", "md=Chromecast", "ve=05"])
        );

        (string? id, string? friendlyName, string? model) = GoogleCastDeviceScanner.ExtractCastInfo(
            msg
        );

        Assert.Equal("a1b2c3d4e5f6", id);
        Assert.Equal("Living Room TV", friendlyName);
        Assert.Equal("Chromecast", model);
    }

    [Fact]
    public void ExtractCastInfo_PrefixMatchIsCaseInsensitive()
    {
        Message msg = new();
        msg.AdditionalRecords.Add(Txt("ID=deadbeef", "FN=Bedroom", "MD=Nest Hub"));

        (string? id, string? friendlyName, string? model) = GoogleCastDeviceScanner.ExtractCastInfo(
            msg
        );

        Assert.Equal("deadbeef", id);
        Assert.Equal("Bedroom", friendlyName);
        Assert.Equal("Nest Hub", model);
    }

    [Fact]
    public void ExtractCastInfo_NoTxtRecords_ReturnsAllNull()
    {
        Message msg = new();

        (string? id, string? friendlyName, string? model) = GoogleCastDeviceScanner.ExtractCastInfo(
            msg
        );

        Assert.Null(id);
        Assert.Null(friendlyName);
        Assert.Null(model);
    }

    [Fact]
    public void ExtractCastInfo_OnlyIdPresent_LeavesFriendlyNameAndModelNull()
    {
        Message msg = new();
        msg.AdditionalRecords.Add(Txt("id=only-this-one"));

        (string? id, string? friendlyName, string? model) = GoogleCastDeviceScanner.ExtractCastInfo(
            msg
        );

        Assert.Equal("only-this-one", id);
        Assert.Null(friendlyName);
        Assert.Null(model);
    }

    [Fact]
    public void ExtractCastInfo_UnknownKeysAreIgnoredWithoutThrowing()
    {
        Message msg = new();
        msg.AdditionalRecords.Add(Txt(["ca=4101", "st=0", "rs=", "bs=FA8FCA1234", "nf=1", "rm="]));

        (string? id, string? friendlyName, string? model) = GoogleCastDeviceScanner.ExtractCastInfo(
            msg
        );

        Assert.Null(id);
        Assert.Null(friendlyName);
        Assert.Null(model);
    }

    [Fact]
    public void ExtractCastInfo_KeysSpreadAcrossMultipleTxtRecords_AreAllCollected()
    {
        Message msg = new();
        msg.AdditionalRecords.Add(Txt("id=split-id"));
        msg.AdditionalRecords.Add(Txt("fn=Split Name"));
        msg.AdditionalRecords.Add(Txt("md=Split Model"));

        (string? id, string? friendlyName, string? model) = GoogleCastDeviceScanner.ExtractCastInfo(
            msg
        );

        Assert.Equal("split-id", id);
        Assert.Equal("Split Name", friendlyName);
        Assert.Equal("Split Model", model);
    }

    [Fact]
    public void IsReachable_NullLanIp_ReturnsFalse()
    {
        GoogleCastDeviceScanner scanner = new(
            NullLogger<GoogleCastDeviceScanner>.Instance,
            new NonBindingMulticastTransport()
        );

        Assert.False(scanner.IsReachable(null));

        scanner.Dispose();
    }

    [Fact]
    public void IsReachable_EmptyLanIp_ReturnsFalse()
    {
        GoogleCastDeviceScanner scanner = new(
            NullLogger<GoogleCastDeviceScanner>.Instance,
            new NonBindingMulticastTransport()
        );

        Assert.False(scanner.IsReachable(string.Empty));

        scanner.Dispose();
    }

    [Fact]
    public void IsReachable_NothingSeenYet_ReturnsFalseForAnyIp()
    {
        GoogleCastDeviceScanner scanner = new(
            NullLogger<GoogleCastDeviceScanner>.Instance,
            new NonBindingMulticastTransport()
        );

        Assert.False(scanner.IsReachable("192.168.1.42"));

        scanner.Dispose();
    }

    [Fact]
    public void GetSeen_NothingSeenYet_ReturnsEmpty()
    {
        GoogleCastDeviceScanner scanner = new(
            NullLogger<GoogleCastDeviceScanner>.Instance,
            new NonBindingMulticastTransport()
        );

        Assert.Empty(scanner.GetSeen());

        scanner.Dispose();
    }

    [Fact]
    public void Dispose_WithoutStart_DoesNotThrow()
    {
        GoogleCastDeviceScanner scanner = new(
            NullLogger<GoogleCastDeviceScanner>.Instance,
            new NonBindingMulticastTransport()
        );

        Exception? ex = Record.Exception(scanner.Dispose);

        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        GoogleCastDeviceScanner scanner = new(
            NullLogger<GoogleCastDeviceScanner>.Instance,
            new NonBindingMulticastTransport()
        );

        Exception? ex = Record.Exception(() =>
        {
            scanner.Dispose();
            scanner.Dispose();
        });

        Assert.Null(ex);
    }
}
