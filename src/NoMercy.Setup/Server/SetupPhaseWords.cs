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

namespace NoMercy.Setup.Server;

/// <summary>
/// The one table of words a user sees for each setup phase. The setup page
/// (/setup/status "label"/"detail"), the terminal and the launcher tray
/// (/manage/status "setup_label"/"setup_detail") all show these strings and keep
/// no copy of their own, so a phase has the same name on every surface.
/// </summary>
public static class SetupPhaseWords
{
    public static string Label(SetupPhase phase)
    {
        return phase switch
        {
            SetupPhase.Unauthenticated => "Waiting for you to sign in",
            SetupPhase.Authenticating => "Signing in",
            SetupPhase.Authenticated => "Signed in",
            SetupPhase.Registering => "Connecting to NoMercy",
            SetupPhase.Registered => "Getting a secure address",
            SetupPhase.CertificateAcquired => "Almost done",
            SetupPhase.Failed => "Setup needs attention",
            SetupPhase.Complete => "Ready",
            _ => phase.ToString(),
        };
    }

    public static string Detail(SetupPhase phase)
    {
        return phase switch
        {
            SetupPhase.Unauthenticated => "Waiting for you to sign in...",
            SetupPhase.Authenticating => "Verifying your credentials...",
            SetupPhase.Authenticated => "Signed in successfully",
            // Registration, address assignment and the certificate poll run as one
            // step; the wording names the real ceiling (the 10-minute timeout in
            // SetupEndpoints.RunPostAuthRegistration).
            SetupPhase.Registering =>
                "Registering server and securing your connection... (this can take up to 10 minutes)",
            SetupPhase.Registered => "Your secure address is ready, finishing up...",
            SetupPhase.CertificateAcquired => "Connection secured",
            SetupPhase.Failed => "Setup could not finish — you can retry.",
            SetupPhase.Complete => "All done — opening NoMercy...",
            _ => "",
        };
    }
}
