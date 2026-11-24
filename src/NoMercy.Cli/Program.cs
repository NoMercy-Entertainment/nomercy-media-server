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
using System.Net.Sockets;
using NoMercy.Cli.Commands;

namespace NoMercy.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Option<string?> pipeOption = new("--pipe", "-p")
        {
            Description = "Named pipe (Windows) or Unix socket path to connect to",
        };

        RootCommand rootCommand = new("NoMercy MediaServer CLI");
        rootCommand.Options.Add(pipeOption);

        ICliClientFactory clientFactory = new CliClientFactory();

        rootCommand.Subcommands.Add(StartCommand.Create(pipeOption, clientFactory));
        rootCommand.Subcommands.Add(StatusCommand.Create(pipeOption, clientFactory));
        rootCommand.Subcommands.Add(LogsCommand.Create(pipeOption, clientFactory));
        rootCommand.Subcommands.Add(StopCommand.Create(pipeOption, clientFactory));
        rootCommand.Subcommands.Add(RestartCommand.Create(pipeOption, clientFactory));
        rootCommand.Subcommands.Add(LogLevelCommand.Create(pipeOption, clientFactory));
        rootCommand.Subcommands.Add(ConfigCommand.Create(pipeOption, clientFactory));
        rootCommand.Subcommands.Add(PluginCommand.Create(pipeOption, clientFactory));
        rootCommand.Subcommands.Add(QueueCommand.Create(pipeOption, clientFactory));
        rootCommand.Subcommands.Add(ResourcesCommand.Create(pipeOption, clientFactory));
        rootCommand.Subcommands.Add(AutoStartCommand.Create(pipeOption, clientFactory));
        rootCommand.Subcommands.Add(UpdateCommand.Create(pipeOption, clientFactory));

        ParseResult parseResult = rootCommand.Parse(args);

        try
        {
            // System.CommandLine's default handler catches everything and prints a raw .NET
            // stack trace, which is how "the server is not running" ended up looking like the
            // CLI itself had crashed. Turning it off lets the handler below classify the
            // failure and return a meaningful exit code.
            return await parseResult.InvokeAsync(
                new InvocationConfiguration { EnableDefaultExceptionHandler = false }
            );
        }
        catch (Exception ex) when (IsServerUnreachable(ex))
        {
            // Every command talks to the server over the management pipe/socket, and when
            // nothing is listening the connect attempt throws straight out of the action.
            // Unhandled, that printed a .NET stack trace — which reads like the CLI is broken
            // rather than like the server simply is not running.
            await Console.Error.WriteLineAsync(
                "NoMercy MediaServer is not running, or is not reachable on its management socket."
            );
            await Console.Error.WriteLineAsync("Start it with: nomercy start");
            return (int)ExitCode.ConnectionError;
        }
    }

    /// <summary>
    /// A failure to reach the management transport, as opposed to a genuine error from a
    /// server that did answer. Timeouts count: connecting to an absent named pipe surfaces as
    /// a cancelled operation rather than a refused connection.
    /// </summary>
    private static bool IsServerUnreachable(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
            if (
                current
                is HttpRequestException
                    or SocketException
                    or TimeoutException
                    or OperationCanceledException
                    or IOException
            )
                return true;

        return false;
    }
}
