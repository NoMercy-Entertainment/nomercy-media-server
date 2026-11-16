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

using NoMercy.NmSystem.Information;

namespace NoMercy.Tests.NmSystem;

[Trait("Category", "Unit")]
public class GpuNameTests
{
    [Theory]
    [InlineData("08:03.0 VGA compatible controller: Matrox Electronics Systems Ltd. MGA G200eW WPCM450 (rev 0a)", "Matrox MGA G200eW WPCM450")]
    [InlineData("01:00.0 VGA compatible controller: NVIDIA Corporation TU104 [GeForce RTX 2080 SUPER] (rev a1)", "NVIDIA GeForce RTX 2080 SUPER")]
    [InlineData("00:02.0 VGA compatible controller: Intel Corporation CoffeeLake-S GT2 [UHD Graphics 630]", "Intel UHD Graphics 630")]
    [InlineData("0b:00.0 VGA compatible controller: Advanced Micro Devices, Inc. [AMD/ATI] Navi 21 [Radeon RX 6800/6800 XT / 6900 XT] (rev c1)", "AMD Radeon RX 6800/6800 XT / 6900 XT")]
    [InlineData("00:0f.0 VGA compatible controller: VMware SVGA II Adapter", "VMware SVGA II Adapter")]
    public void LinuxDisplayName_KeepsVendorAndModelOnly(string lspciLine, string expected)
    {
        Gpu.LinuxDisplayName(lspciLine).Should().Be(expected);
    }

    [Fact]
    public void LinuxDisplayName_LeavesAPlainNameAlone()
    {
        Gpu.LinuxDisplayName("NVIDIA GeForce RTX 2080 SUPER").Should().Be("NVIDIA GeForce RTX 2080 SUPER");
    }
}
