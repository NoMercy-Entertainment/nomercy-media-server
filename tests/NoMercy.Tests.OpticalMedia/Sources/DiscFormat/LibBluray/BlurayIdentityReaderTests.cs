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

using NoMercy.DiscFormat.Abstractions.Disc;
using NoMercy.DiscFormat.LibBluray;
using NoMercy.DiscFormat.LibBluray.Identity;

namespace NoMercy.DiscFormat.Tests.LibBluray;

public sealed class BlurayIdentityReaderTests
{
    private sealed class FakeLibBluray : ILibBluray
    {
        private readonly BlurayDiscInfo _info;

        public FakeLibBluray(BlurayDiscInfo info)
        {
            _info = info;
        }

        public BlurayDiscInfo? DiscInfo { get; private set; }
        public uint TitleCount { get; private set; }

        public void Open(string devicePath, string? keyfilePath = null)
        {
            DiscInfo = _info;
            TitleCount = _info.NumTitles;
        }

        public void Dispose() { }
    }

    private static BlurayDiscInfo Info(string discIdHex, uint bdjTitles, uint hdmvTitles)
    {
        return new BlurayDiscInfo
        {
            BlurayDetected = true,
            AacsDetected = true,
            AacsHandled = true,
            BdplusDetected = false,
            FirstPlaySupported = true,
            TopMenuSupported = true,
            NumTitles = bdjTitles + hdmvTitles,
            NumHdmvTitles = hdmvTitles,
            NumBdjTitles = bdjTitles,
            NumUnsupportedTitles = 0,
            AacsErrorCode = 0,
            AacsMkbv = 0,
            DiscName = "Sample",
            UdfVolumeId = "SAMPLE_DISC",
            DiscIdHex = discIdHex,
        };
    }

    private static DiscTranspileRequest Request()
    {
        return new DiscTranspileRequest
        {
            Kind = DiscKind.Bdj,
            DiscTitle = "irrelevant-folder",
            DevicePath = "D:/",
        };
    }

    [Fact]
    public void Handles_bd_kinds_only()
    {
        BlurayIdentityReader reader = new(() => new FakeLibBluray(Info("aa", 4, 0)));

        Assert.True(reader.CanHandle(DiscKind.Bdj));
        Assert.True(reader.CanHandle(DiscKind.Hdmv));
        Assert.False(reader.CanHandle(DiscKind.Dvd));
    }

    [Fact]
    public void Different_aacs_disc_id_yields_different_identity()
    {
        string airbenderId = "30891416659edac92cc016ea9a2e01c9e3dcb446";
        string korraId = "ee924f3c1bcd06f68039e203fa78863e58d8906f";

        BlurayIdentityReader airbender = new(() => new FakeLibBluray(Info(airbenderId, 4, 0)));
        BlurayIdentityReader korra = new(() => new FakeLibBluray(Info(korraId, 4, 0)));

        DiscIdentity airbenderIdentity = airbender.Read(Request());
        DiscIdentity korraIdentity = korra.Read(Request());

        Assert.NotEqual(airbenderIdentity.Id, korraIdentity.Id);
    }

    [Fact]
    public void Same_disc_yields_same_identity()
    {
        string discId = "30891416659edac92cc016ea9a2e01c9e3dcb446";
        BlurayIdentityReader reader = new(() => new FakeLibBluray(Info(discId, 4, 0)));

        DiscIdentity first = reader.Read(Request());
        DiscIdentity second = reader.Read(Request());

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public void Provider_prefix_reflects_the_dominant_title_kind()
    {
        BlurayIdentityReader bdj = new(() => new FakeLibBluray(Info("aa", 4, 1)));
        BlurayIdentityReader hdmv = new(() => new FakeLibBluray(Info("aa", 0, 3)));

        Assert.StartsWith("bdj:", bdj.Read(Request()).Id);
        Assert.StartsWith("hdmv:", hdmv.Read(Request()).Id);
    }
}
