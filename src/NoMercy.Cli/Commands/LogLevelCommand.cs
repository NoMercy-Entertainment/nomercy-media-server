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

using System.CommandLine;

namespace NoMercy.Cli.Commands;

internal static class LogLevelCommand
{
    private static readonly string[] Levels =
    [
        "Verbose",
        "Debug",
        "Information",
        "Warning",
        "Error",
        "Fatal",
    ];

    public static Command Create(Option<string?> pipeOption, ICliClientFactory clientFactory)
    {
        Argument<string> levelArgument = new("level")
        {
            Description = "The new log level: " + string.Join(", ", Levels),
        };
        levelArgument.Validators.Add(result =>
        {
            string value = result.GetValueOrDefault<string>() ?? string.Empty;
            if (!Levels.Any(level => level.Equals(value, StringComparison.OrdinalIgnoreCase)))
                result.AddError(
                    $"Unknown log level '{value}'. Use one of: {string.Join(", ", Levels)}."
                );
        });

        Command command = new("loglevel") { Description = "Switch the server's log level" };
        command.Arguments.Add(levelArgument);

        command.SetAction(
            async (parseResult, ct) =>
            {
                string input = parseResult.GetValue(levelArgument)!;
                string level = Levels.First(l =>
                    l.Equals(input, StringComparison.OrdinalIgnoreCase)
                );
                string? pipe = parseResult.GetValue(pipeOption);
                using ICliClient client = clientFactory.Create(pipe);
                bool ok = await client.PostAsync(ApiRoutes.LogLevel + "?level=" + level, null, ct);

                if (!ok)
                    return (int)ExitCode.ServerError;

                Console.WriteLine($"Log level set to {level}.");
                return (int)ExitCode.Success;
            }
        );

        return command;
    }
}
