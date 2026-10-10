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

using System.Text;
using NoMercy.DiscFormat.Abstractions.Disc;
using NoMercy.DiscFormat.Dvd.Identity;

namespace NoMercy.DiscFormat.Tests.Dvd;

public sealed class DvdIdentityReaderTests
{
    private static byte[] FakeIfo(string marker)
    {
        byte[] sector = new byte[2048];
        byte[] sig = Encoding.ASCII.GetBytes(marker);
        Array.Copy(sig, sector, Math.Min(sig.Length, sector.Length));
        return sector;
    }

    private static DiscTranspileRequest RequestWith(params (string Name, string Marker)[] ifos)
    {
        Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string marker) in ifos)
        {
            files[name] = FakeIfo(marker);
        }

        return new DiscTranspileRequest
        {
            Kind = DiscKind.Dvd,
            DiscTitle = "irrelevant-folder-name",
            IfoFiles = files,
        };
    }

    [Fact]
    public void Handles_dvd_only()
    {
        DvdIdentityReader reader = new();

        Assert.True(reader.CanHandle(DiscKind.Dvd));
        Assert.False(reader.CanHandle(DiscKind.Hdmv));
        Assert.False(reader.CanHandle(DiscKind.Bdj));
    }

    [Fact]
    public void Same_ifo_set_yields_same_id_regardless_of_folder_name()
    {
        DvdIdentityReader reader = new();

        DiscIdentity first = reader.Read(
            RequestWith(("VIDEO_TS.IFO", "VMG-A"), ("VTS_01_0.IFO", "VTS-1"))
        );
        DiscIdentity second = reader.Read(
            RequestWith(("VIDEO_TS.IFO", "VMG-A"), ("VTS_01_0.IFO", "VTS-1"))
        );

        Assert.Equal(first.Id, second.Id);
        Assert.StartsWith("dvd:", first.Id);
    }

    [Fact]
    public void Different_structure_yields_different_id()
    {
        DvdIdentityReader reader = new();

        DiscIdentity discA = reader.Read(
            RequestWith(("VIDEO_TS.IFO", "VMG-A"), ("VTS_01_0.IFO", "VTS-1"))
        );
        DiscIdentity discB = reader.Read(
            RequestWith(("VIDEO_TS.IFO", "VMG-B"), ("VTS_01_0.IFO", "VTS-1"))
        );

        Assert.NotEqual(discA.Id, discB.Id);
    }

    [Fact]
    public void Vts_order_does_not_change_the_id()
    {
        DvdIdentityReader reader = new();

        DiscIdentity ordered = reader.Read(
            RequestWith(("VIDEO_TS.IFO", "VMG"), ("VTS_01_0.IFO", "one"), ("VTS_02_0.IFO", "two"))
        );
        DiscIdentity shuffled = reader.Read(
            RequestWith(("VTS_02_0.IFO", "two"), ("VIDEO_TS.IFO", "VMG"), ("VTS_01_0.IFO", "one"))
        );

        Assert.Equal(ordered.Id, shuffled.Id);
    }
}
