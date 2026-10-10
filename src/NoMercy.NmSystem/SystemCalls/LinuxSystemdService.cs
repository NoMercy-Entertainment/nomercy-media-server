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

namespace NoMercy.NmSystem.SystemCalls;

internal sealed class LinuxSystemdService(
    Func<string, IReadOnlyList<string>, Task<Shell.ExecResult>> run
)
{
    private const string UnitName = "nomercy-mediaserver.service";

    public static LinuxSystemdService Current { get; } =
        new((executable, arguments) => Shell.ExecAsync(executable, arguments));

    public void Enable(string userName)
    {
        RunRequired("systemctl", ["--user", "daemon-reload"]);
        RunRequired("systemctl", ["--user", "enable", UnitName]);
        RunRequired("loginctl", ["enable-linger", userName]);
    }

    public void Disable()
    {
        RunRequired("systemctl", ["--user", "disable", UnitName]);
        RunRequired("systemctl", ["--user", "daemon-reload"]);
    }

    public bool IsEnabled(string userName)
    {
        Shell.ExecResult unit = Run("systemctl", ["--user", "is-enabled", UnitName]);
        if (!unit.Success || unit.StandardOutput.Trim() != "enabled")
            return false;

        Shell.ExecResult linger = Run(
            "loginctl",
            ["show-user", userName, "-p", "Linger", "--value"]
        );
        return linger.Success && linger.StandardOutput.Trim() == "yes";
    }

    private void RunRequired(string executable, IReadOnlyList<string> arguments)
    {
        Shell.ExecResult result = Run(executable, arguments);
        if (!result.Success)
            throw new InvalidOperationException(
                $"{executable} {string.Join(" ", arguments)} failed: {result.StandardError}"
            );
    }

    private Shell.ExecResult Run(string executable, IReadOnlyList<string> arguments) =>
        run(executable, arguments).GetAwaiter().GetResult();
}
