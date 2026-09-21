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
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Runtime;

/// <summary>Starts the child for real.</summary>
public class SystemProcessStarter : IPluginProcessStarter
{
    public IPluginProcessHandle Start(PluginProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        ProcessStartInfo start = new()
        {
            FileName = request.Binary,
            // Never through a shell: UseShellExecute would re-interpret the
            // arguments and reintroduce the PATH search the approved-binary
            // check exists to remove.
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
        };

        // Added to the list rather than joined into a string, so a path with a
        // space in it is one argument and not two.
        foreach (string argument in request.Arguments)
            start.ArgumentList.Add(argument);

        foreach (
            (string key, string value) in request.Environment ?? new Dictionary<string, string>()
        )
            start.Environment[key] = value;

        Process process =
            Process.Start(start)
            ?? throw new InvalidOperationException(
                $"The operating system started no process for {request.Binary}."
            );

        return new PluginProcessHandle(process);
    }
}
