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

using System.Diagnostics;

namespace NoMercy.Plugin.Cli;

internal static class DotnetProcess
{
    /// <summary>
    /// Runs dotnet and returns what it printed. Output is collected rather than
    /// streamed so a failed pack can quote the compiler's own words once.
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(
        IEnumerable<string> arguments,
        string workingDirectory,
        string? packagesFolder,
        CancellationToken ct
    )
    {
        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        if (packagesFolder is not null)
            start.Environment["NUGET_PACKAGES"] = packagesFolder;

        using Process process =
            Process.Start(start) ?? throw new PackException("dotnet could not be started.");

        Task<string> output = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, await output + await error);
    }

    /// <summary>The lines MSBuild marks as errors, without the progress chatter around them.</summary>
    public static string Errors(string output)
    {
        IEnumerable<string> lines = output
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Contains(" error ", StringComparison.Ordinal))
            .Distinct();

        string joined = string.Join('\n', lines);
        return joined.Length > 0 ? joined : output.Trim();
    }
}
