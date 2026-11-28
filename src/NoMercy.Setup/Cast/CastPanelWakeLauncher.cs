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

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NoMercy.Networking.Cast;

namespace NoMercy.Setup.Cast;

/// <summary>
/// Owns MusicHub.ChangeDeviceCommand's server-side Cast panel-wake LAUNCH
/// (HDMI-CEC One Touch Play via sharpcaster). This is the single gate: firing
/// the LAUNCH while the target is already live on MusicHub lets cast_shell
/// miss the running APK on the Cast Connect path and fall back to the Web
/// Receiver, replacing the app mid-playback and getting the session
/// liveness-ended. <see cref="ShouldFireCastWake"/> must stay the only
/// condition under which <see cref="LaunchIfColdAsync"/> touches
/// <see cref="IChromeCastService"/> — do not add an unconditional call path.
/// The optional follow-up LAUNCH inside <see cref="LaunchIfColdAsync"/> (the
/// device-bus-confirmed handoff from Web Receiver to the real app) is still
/// inside this same gate — it only ever runs as a continuation of an already
/// cold-target call, never as its own entry point.
/// </summary>
public class CastPanelWakeLauncher(
    IChromeCastService chromeCast,
    ILogger<CastPanelWakeLauncher> logger,
    // Defaults: ~12s at 1s apart. ServerBusClient's own reconnect backoff
    // starts at 1s, so a device that was going to come back at all after a
    // cold CEC wake almost always does inside this window; one that doesn't
    // is genuinely still cold, not just slow, and the follow-up is skipped
    // rather than held open indefinitely. Constructor-injectable so a test
    // can prove the "never comes online" path without a real 12-second wait.
    int followUpPollIntervalMs = 1_000,
    int followUpPollAttempts = 12
)
{
    public static bool ShouldFireCastWake(bool targetIsLive) => !targetIsLive;

    // A TV with the NoMercy app that was launched a moment ago is still starting its app.
    private const long RecentLaunchWindowMs = 20_000;
    private readonly ConcurrentDictionary<string, long> _lastLaunchAt = new();

    /// <summary>
    /// resolveLaunchData is deferred so a live target never pays for the
    /// token-exchange call it would just discard.
    ///
    /// isTargetOnlineNow, when given, is polled AFTER a cold (useAndroidReceiver:
    /// false) LAUNCH to hand off from the safe Web Receiver placeholder to the
    /// real app once the device-bus confirms the APK actually came back —
    /// see the follow-up block below for why the first LAUNCH can't just
    /// claim the APK is there.
    /// </summary>
    public async Task<CastLaunchOutcome> LaunchIfColdAsync(
        bool targetIsLive,
        string? targetIp,
        bool useAndroidReceiver,
        Func<Task<LaunchCustomData?>> resolveLaunchData,
        Func<bool>? isTargetOnlineNow = null,
        Func<bool>? isAppOnlineNow = null,
        bool targetHasApp = false
    )
    {
        if (!ShouldFireCastWake(targetIsLive))
            return CastLaunchOutcome.SkippedLive;

        // The handoff itself still proceeds without an address; the launch is what is lost.
        if (targetIp is null)
        {
            logger.LogWarning(
                "Cast launch skipped: no LAN address recorded for the target TV, so it is not woken"
            );
            return CastLaunchOutcome.NoAddress;
        }

        long now = Environment.TickCount64;
        if (
            targetHasApp
            && _lastLaunchAt.TryGetValue(targetIp, out long lastAt)
            && now - lastAt < RecentLaunchWindowMs
        )
        {
            logger.LogInformation(
                "Cast launch skipped: {TargetIp} was launched {Seconds}s ago and its app is still starting",
                targetIp,
                (now - lastAt) / 1000
            );
            return CastLaunchOutcome.SkippedRecent;
        }

        try
        {
            string? receiverName = await chromeCast.FindReceiverNameByIpAsync(targetIp);
            if (string.IsNullOrEmpty(receiverName))
            {
                logger.LogWarning(
                    "No Chromecast receiver discovered at {TargetIp} — panel won't wake via CEC",
                    targetIp
                );
                return CastLaunchOutcome.NoReceiver;
            }

            logger.LogInformation(
                "Cast launch requested at {TargetIp} (receiver {Receiver}) transport: {Transport}",
                targetIp,
                receiverName,
                useAndroidReceiver ? "cast connect app" : "web receiver"
            );

            _lastLaunchAt[targetIp] = now;
            LaunchCustomData? launchData = await resolveLaunchData();
            if (launchData is null)
            {
                logger.LogWarning(
                    "Cast token mint failed for {TargetIp} — falling back to LAUNCH without customData",
                    targetIp
                );
            }

            // SelectChromecast connects/reuses the pool entry for this specific
            // receiver. useAndroidReceiver is true only when the APK is reachable
            // on this TV (registered with the bus registry); otherwise cast_shell
            // would try the Cast Connect path, fail to find the APK, and fall back
            // to Web Receiver — that fallback path drops customData and the
            // receiver hangs on its splash. Going straight to Web Receiver
            // preserves customData.
            await chromeCast.SelectChromecast(receiverName);
            await chromeCast.LaunchAndroidReceiver(
                receiverName,
                launchData,
                useAndroidReceiver: useAndroidReceiver
            );

            // The follow-up handoff. The LAUNCH above deliberately claimed
            // useAndroidReceiver=false whenever the bus registry didn't already
            // show the device online — at the instant of a cold wake it never
            // does, by definition. A false "true" claim isn't a safe fallback:
            // cast_shell tries the Cast Connect path, doesn't find the APK,
            // and hangs on a blank splash with customData dropped, worse than
            // the Web Receiver it would otherwise land on cleanly. So instead:
            // land safely first, then re-LAUNCH once the device-bus proves the
            // APK is genuinely reachable, handing off from the placeholder to
            // the real app. Skipped entirely when the first LAUNCH already
            // claimed the APK (nothing to hand off from) or the caller gave no
            // way to check (isTargetOnlineNow is null).
            if (!useAndroidReceiver && isTargetOnlineNow is not null)
            {
                bool cameOnline = await PollForOnlineAsync(isTargetOnlineNow);
                if (cameOnline)
                {
                    await chromeCast.LaunchAndroidReceiver(
                        receiverName,
                        launchData,
                        useAndroidReceiver: true
                    );
                }
                else
                {
                    logger.LogWarning(
                        "Cast launch follow-up skipped: {TargetIp} never came online on the device bus",
                        targetIp
                    );
                }
            }

            // No app on the TV: Cast Connect falls back to a Web Receiver without the token.
            // Not awaited, so the caller's wake answer stays fast.
            if (useAndroidReceiver && isAppOnlineNow is not null)
                _ = FallBackToWebReceiverAsync(receiverName, launchData, isAppOnlineNow);

            logger.LogInformation(
                "Cast launch result: launched at {TargetIp} (receiver {Receiver})",
                targetIp,
                receiverName
            );
            return CastLaunchOutcome.Launched;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Cast launch result: failed for {TargetIp} exception={Exception}: {Message}",
                targetIp,
                ex.GetType().Name,
                ex.Message
            );
            return CastLaunchOutcome.Failed;
        }
    }

    private async Task FallBackToWebReceiverAsync(
        string receiverName,
        LaunchCustomData? launchData,
        Func<bool> isAppOnlineNow
    )
    {
        try
        {
            if (await PollForOnlineAsync(isAppOnlineNow))
                return;

            logger.LogInformation(
                "NoMercy app of '{Receiver}' never came online — launching the Web Receiver",
                receiverName
            );
            await chromeCast.LaunchAndroidReceiver(
                receiverName,
                launchData,
                useAndroidReceiver: false
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Web Receiver fallback launch failed for '{Receiver}': {Message}",
                receiverName,
                ex.Message
            );
        }
    }

    private async Task<bool> PollForOnlineAsync(Func<bool> isTargetOnlineNow)
    {
        for (int attempt = 0; attempt < followUpPollAttempts; attempt++)
        {
            if (isTargetOnlineNow())
                return true;
            await Task.Delay(followUpPollIntervalMs);
        }

        return isTargetOnlineNow();
    }
}
