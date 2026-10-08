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
/// Requirement: in a non-interactive environment (Docker, systemd, Windows service —
/// the common self-hosted deployment shape), <see cref="SetupTerminalUi"/> must fall
/// back to plain log lines instead of touching the console, and every method must
/// treat that as a no-op rather than throwing. The QR-rendering logic itself
/// (<see cref="SetupTerminalUi.GenerateAsciiQr"/>) is pure and Console-free, so it is
/// tested directly against its "fits" / "too narrow" / bad-input behavior.
/// </summary>
/// <remarks>
/// <see cref="SetupTerminalUi.ForceInteractiveForTests"/> is the seam added specifically
/// so these tests do not depend on whatever Console state the test runner happens to
/// have (a CI runner's stdout is typically redirected regardless of what a given test
/// wants to exercise). The interactive Draw()/ShowProgress()/ShowComplete() bodies
/// beyond the IsInteractiveTerminal gate require a real attached terminal to observe
/// Console.Clear()/WindowWidth succeeding — see the itemized note at the bottom of
/// this file for exactly which lines that leaves uncovered.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class SetupTerminalUiTests : IDisposable
{
    public void Dispose()
    {
        SetupTerminalUi.ForceInteractiveForTests = null;
    }

    // ── IsInteractiveTerminal (via the test seam) ────────────────────────────

    [Fact]
    public void IsInteractiveTerminal_ForcedTrue_ReturnsTrue()
    {
        SetupTerminalUi.ForceInteractiveForTests = true;

        Assert.True(SetupTerminalUi.IsInteractiveTerminal);
    }

    [Fact]
    public void IsInteractiveTerminal_NoOverride_EvaluatesRealAmbientConsoleState()
    {
        // With the override cleared, this exercises the REAL check (UserInteractive,
        // IsOutputRedirected, and the WindowWidth try/catch) against whatever console
        // state the test host actually has — the point of this test is that it
        // completes and returns a bool without throwing, not a specific value (that
        // depends on the environment running the suite).
        SetupTerminalUi.ForceInteractiveForTests = null;

        bool result = SetupTerminalUi.IsInteractiveTerminal;

        Assert.IsType<bool>(result);
    }

    [Fact]
    public void IsInteractiveTerminal_ForcedFalse_ReturnsFalse()
    {
        SetupTerminalUi.ForceInteractiveForTests = false;

        Assert.False(SetupTerminalUi.IsInteractiveTerminal);
    }

    // ── Non-interactive behavior (production-common: Docker/systemd/service) ───

    [Fact]
    public void Show_NonInteractive_LogsAndReturnsWithoutThrowing()
    {
        SetupTerminalUi.ForceInteractiveForTests = false;
        using SetupTerminalUi ui = new();

        ui.Show(
            "https://auth.nomercy.tv/device?code=ABCD",
            "https://auth.nomercy.tv/device",
            "ABCD-1234",
            SetupAddress.Resolve("192.168.2.10", 7626, inContainer: false)
        );
    }

    [Fact]
    public void SetStatus_NonInteractive_DoesNotThrow()
    {
        SetupTerminalUi.ForceInteractiveForTests = false;
        using SetupTerminalUi ui = new();

        ui.SetStatus("Waiting for you to sign in...");
    }

    [Fact]
    public void ShowProgress_NonInteractive_ReturnsImmediatelyWithoutThrowing()
    {
        SetupTerminalUi.ForceInteractiveForTests = false;
        using SetupTerminalUi ui = new();

        ui.ShowProgress("Registering", "Connecting your server to NoMercy...");
    }

    [Fact]
    public void ShowComplete_NonInteractive_ReturnsImmediatelyWithoutThrowing()
    {
        SetupTerminalUi.ForceInteractiveForTests = false;
        using SetupTerminalUi ui = new();

        ui.ShowComplete("https://abc123.nomercy.app");
    }

    [Fact]
    public void Dispose_NonInteractive_DoesNotThrow()
    {
        SetupTerminalUi.ForceInteractiveForTests = false;
        SetupTerminalUi ui = new();

        ui.Dispose();
        // Double-dispose must also be safe (StopResizeWatcher tolerates
        // ObjectDisposedException from an already-cancelled/disposed CTS).
        ui.Dispose();
    }

    [Fact]
    public void ShowProgress_ForcedInteractive_DoesNotThrow_RegardlessOfConsoleAvailability()
    {
        // Forcing IsInteractiveTerminal=true in the test host still routes through the
        // real Console.Clear()/SetCursorPosition calls — whatever the actual outcome
        // (succeeds against an inherited console, or throws IOException that
        // ShowProgress's own try/catch absorbs), this must never propagate.
        SetupTerminalUi.ForceInteractiveForTests = true;
        using SetupTerminalUi ui = new();

        ui.ShowProgress("Registered", "Setting up your server address...");
    }

    [Fact]
    public void ShowComplete_ForcedInteractive_DoesNotThrow_RegardlessOfConsoleAvailability()
    {
        SetupTerminalUi.ForceInteractiveForTests = true;
        using SetupTerminalUi ui = new();

        ui.ShowComplete("https://abc123.nomercy.app");
    }

    [Fact]
    public void Show_ForcedInteractive_DoesNotThrow_RegardlessOfConsoleAvailability()
    {
        SetupTerminalUi.ForceInteractiveForTests = true;
        SetupTerminalUi ui = new();

        ui.Show(
            "https://auth.nomercy.tv/device?code=ABCD",
            "https://auth.nomercy.tv/device",
            "ABCD-1234",
            SetupAddress.Resolve("192.168.2.10", 7626, inContainer: false)
        );

        // Give the resize watcher's background loop at least one 250ms tick, then
        // dispose and wait past its poll interval again — StartResizeWatcher's loop
        // is a fire-and-forget Task.Run tied to this instance's own CTS, so without
        // this second wait it can still be mid-Draw() when the NEXT test's Console
        // calls run, corrupting that test's own Console.Clear/SetCursorPosition
        // sequence (observed: ShowProgress/ShowComplete's SetCursorPosition call
        // throwing when a leaked watcher from this test was still active).
        Thread.Sleep(300);
        ui.Dispose();
        Thread.Sleep(300);
    }

    // ── GenerateAsciiQr (pure, no Console dependency) ────────────────────────

    [Fact]
    public void GenerateAsciiQr_WideTerminal_ReturnsNonEmptyLines()
    {
        string[] lines = SetupTerminalUi.GenerateAsciiQr(
            "https://auth.nomercy.tv/device?code=ABCD",
            terminalWidth: 200
        );

        Assert.NotEmpty(lines);
        // Every line must be the same width (a real block-character QR grid).
        int width = lines[0].Length;
        Assert.All(lines, line => Assert.Equal(width, line.Length));
    }

    [Fact]
    public void GenerateAsciiQr_TooNarrowTerminal_ReturnsEmpty()
    {
        string[] lines = SetupTerminalUi.GenerateAsciiQr(
            "https://auth.nomercy.tv/device?code=ABCD",
            terminalWidth: 1
        );

        Assert.Empty(lines);
    }

    [Fact]
    public void GenerateAsciiQr_EmptyText_StillProducesAValidGrid_DoesNotThrow()
    {
        // QRCodeGenerator does not throw for a zero-length payload — it renders a
        // valid (near-empty-pattern) QR grid. This documents that behavior rather
        // than asserting the exception-catch branch, which needs a genuinely
        // malformed input to reach (see the next test).
        string[] lines = SetupTerminalUi.GenerateAsciiQr(string.Empty, terminalWidth: 200);

        Assert.NotNull(lines);
    }

    [Fact]
    public void GenerateAsciiQr_TextTooLongForAnyQrVersion_ReturnsEmptyViaCatchBranch()
    {
        // QR codes cap out at ~2953 bytes for the lowest error-correction level —
        // exceeding it makes QRCodeGenerator.CreateQrCode itself throw, which is what
        // GenerateAsciiQr's own catch(Exception) branch converts into an empty result.
        string[] lines = SetupTerminalUi.GenerateAsciiQr(new string('x', 5000), terminalWidth: 300);

        Assert.Empty(lines);
    }

    [Fact]
    public void GenerateAsciiQr_LongUrl_StillFitsOrGracefullyEmpties()
    {
        string longUrl =
            "https://auth.nomercy.tv/device?code=ABCD-1234&extra_param=" + new string('x', 200);

        string[] lines = SetupTerminalUi.GenerateAsciiQr(longUrl, terminalWidth: 300);

        // A very long payload produces a bigger QR grid — either it still fits the
        // generous terminal width, or GenerateAsciiQr's own width check empties it.
        // Either outcome must not throw; assert the method actually returned.
        Assert.NotNull(lines);
    }
}

// NOTE ON RESIDUAL COVERAGE: SetupTerminalUi.cs — Draw()'s body past
// `Console.Clear(); Console.SetCursorPosition(0, 0);` (the QR-centering and
// Console.WriteLine calls, roughly lines 219-260), and the equivalent bodies in
// ShowProgress()/ShowComplete() past their own Console.Clear() call, are gated on
// whether Console.Clear()/Console.SetCursorPosition actually succeed in the test-host
// process — empirically observed (during development of this file) to be
// NON-DETERMINISTIC across runs and across which SetupTerminalUi method call happens
// first in the process (a still-unwound background resize-watcher thread from a prior
// Show() call appears to race a later ShowProgress()/ShowComplete() call's own
// SetCursorPosition, making it throw IOException where it otherwise wouldn't). This
// is not reachable deterministically without a real, exclusively-owned terminal
// session — the tests above still lock the REQUIRED behavior (never throws, whichever
// branch fires) via ForceInteractiveForTests=true, which is what actually matters in
// production (a Windows service or systemd unit hits the IOException branch every
// time; an interactive terminal hits the drawing branch every time — this suite
// cannot control which one a shared CI/test-runner console presents). Same residue
// category as documented for ConsoleMessages/ConsoleQrCode elsewhere in this suite.
