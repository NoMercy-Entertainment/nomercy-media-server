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

namespace NoMercy.Plugin.Cli;

public static class CliArguments
{
    private static readonly string[] OptionsWithValue = ["--output", "--feed"];

    /// <summary>
    /// The folder or zip a verb works on: the first argument after the verb that
    /// is neither a flag nor a flag's value. Flags may come before or after it.
    /// </summary>
    public static string Target(string[] args)
    {
        for (int index = 1; index < args.Length; index++)
        {
            if (!args[index].StartsWith('-'))
                return args[index];

            if (OptionsWithValue.Contains(args[index]))
                index++;
        }

        return Directory.GetCurrentDirectory();
    }
}
