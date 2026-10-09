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

using NoMercy.Setup.Ui;

namespace NoMercy.Tests.Setup.Ui;

/// <summary>
/// Requirement: in a non-interactive environment, <see cref="ConsoleQrCode.Display"/>
/// must log the device-auth info instead of drawing anything, and the legacy
/// single-argument overload must parse a display URI and user code out of the
/// complete verification URI — falling back gracefully (never throwing) when that URI
/// is malformed.
/// </summary>
/// <remarks>
/// The interactive branch is deliberately NOT exercised here: it constructs a
/// <see cref="SetupTerminalUi"/> and intentionally never disposes it (see the
/// production comment on <c>Display</c> — "Keep the UI alive until the process ends"),
/// which starts a background resize-watcher thread with no way to stop it from a test.
/// Forcing that branch would leak a live thread for the rest of this test process and
/// risk exactly the Console-state bleed-through documented in
/// <c>SetupTerminalUiTests</c>.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class ConsoleQrCodeTests : IDisposable
{
    public ConsoleQrCodeTests() => SetupTerminalUi.ForceInteractiveForTests = false;

    public void Dispose() => SetupTerminalUi.ForceInteractiveForTests = null;

    [Fact]
    public void Display_FourArgs_NonInteractive_LogsWithoutThrowing()
    {
        ConsoleQrCode.Display(
            "https://auth.nomercy.tv/device?code=ABCD",
            "https://auth.nomercy.tv/device",
            "ABCD-1234",
            SetupAddress.Resolve("192.168.2.10", 7626, inContainer: false)
        );
    }

    [Fact]
    public void Display_ThreeArgs_NonInteractive_BuildsSetupPageUrlFromInternalPort()
    {
        ConsoleQrCode.Display(
            "https://auth.nomercy.tv/device?code=ABCD",
            "https://auth.nomercy.tv/device",
            "ABCD-1234"
        );
    }

    [Fact]
    public void Display_LegacyOverload_ValidUri_ParsesDisplayUriAndUserCode()
    {
        ConsoleQrCode.Display("https://auth.nomercy.tv:8443/device?user_code=WXYZ-5678&extra=1");
    }

    [Fact]
    public void Display_LegacyOverload_DefaultPort_OmitsPortFromDisplayUri()
    {
        ConsoleQrCode.Display("https://auth.nomercy.tv/device?user_code=ABCD-0001");
    }

    [Fact]
    public void Display_LegacyOverload_NoUserCodeQueryParam_DoesNotThrow()
    {
        ConsoleQrCode.Display("https://auth.nomercy.tv/device");
    }

    [Fact]
    public void Display_LegacyOverload_MalformedUri_FallsBackWithoutThrowing()
    {
        ConsoleQrCode.Display("not a valid uri at all!!");
    }

    [Fact]
    public void Display_LegacyOverload_EmptyString_DoesNotThrow()
    {
        ConsoleQrCode.Display(string.Empty);
    }
}
