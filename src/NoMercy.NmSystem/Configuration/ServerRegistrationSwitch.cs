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

namespace NoMercy.NmSystem.Configuration;

/// <summary>
/// Dev-only switch for a second server copy on a machine that already runs one.
/// The device id is a hardware id, so every copy on a machine shares it, and a copy
/// that registers replaces the first copy's address and tunnel on nomercy.tv.
/// With NOMERCY_SKIP_REGISTER=true a copy never registers, assigns, announces its
/// address, asks for a tunnel or opens one.
/// </summary>
public static class ServerRegistrationSwitch
{
    public const string VariableName = "NOMERCY_SKIP_REGISTER";

    // Read on every call so a test can flip it; the value is a process-start setting.
    public static bool IsSet =>
        Environment.GetEnvironmentVariable(VariableName) is { } value
        && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");
}
