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
using System.Drawing;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using NoMercy.Cli.Models;
using NoMercy.Networking.Discovery;
using NoMercy.NmSystem.Extensions;
using Pastel;

namespace NoMercy.Cli.Commands;

internal static partial class LogsCommand
{
    private static DateTime _lastEntryTime = DateTime.MinValue;

    [GeneratedRegex(
        @"(\x1b|\\u001[bB])\[[0-9;]*[A-Za-z]",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex AnsiEscapeRegex();

    public static Command Create(Option<string?> pipeOption, ICliClientFactory clientFactory)
    {
        Option<int> tailOption = new("--tail", "-n")
        {
            Description = "Number of log entries to show",
            DefaultValueFactory = _ => 100,
        };

        Option<bool> followOption = new("--follow", "-f")
        {
            Description = "Stream logs in real-time",
            DefaultValueFactory = _ => false,
        };

        Option<string?> levelOption = new("--level")
        {
            Description = "Filter by log level (e.g. Information,Warning,Error)",
        };

        Option<string?> typeOption = new("--type") { Description = "Filter by log type" };

        Command command = new("logs") { Description = "View server logs" };
        command.Options.Add(tailOption);
        command.Options.Add(followOption);
        command.Options.Add(levelOption);
        command.Options.Add(typeOption);

        command.SetAction(
            async (parseResult, ct) =>
            {
                string? pipe = parseResult.GetValue(pipeOption);
                int tail = parseResult.GetValue(tailOption);
                bool follow = parseResult.GetValue(followOption);
                string? level = parseResult.GetValue(levelOption);
                string? type = parseResult.GetValue(typeOption);

                using ICliClient client = clientFactory.Create(pipe);

                // Fetch initial batch
                string query = BuildQuery(tail, level, type);
                List<LogEntryResponse>? logs = await client.GetAsync<List<LogEntryResponse>>(
                    $"{ApiRoutes.Logs}{query}",
                    ct
                );

                if (logs is null)
                {
                    await Console.Error.WriteLineAsync("Could not connect to server.");
                    return (int)ExitCode.ServerError;
                }

                foreach (LogEntryResponse entry in logs)
                    PrintEntry(entry);

                if (!follow)
                    return (int)ExitCode.Success;

                // Stream via SSE
                using IpcClient ipc = new(pipe);
                try
                {
                    using HttpResponseMessage response = await ipc.GetStreamAsync(
                        $"{ApiRoutes.LogsStream}?backfill=0",
                        ct
                    );

                    await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
                    using StreamReader reader = new(stream);

                    while (!ct.IsCancellationRequested)
                    {
                        string? line = await reader.ReadLineAsync(ct);
                        if (line is null)
                            break;
                        if (!line.StartsWith("data: "))
                            continue;

                        string json = line[6..];
                        LogEntryResponse? entry;
                        try
                        {
                            entry = JsonConvert.DeserializeObject<LogEntryResponse>(json);
                        }
                        catch (JsonException)
                        {
                            // Truncated SSE line during a server restart — skip
                            // and wait for the next clean event instead of
                            // killing the whole `nomercy logs` session.
                            continue;
                        }
                        if (entry is null)
                            continue;

                        // Apply client-side filters
                        if (
                            !string.IsNullOrWhiteSpace(level)
                            && !level
                                .Split(',')
                                .Any(l =>
                                    string.Equals(
                                        l.Trim(),
                                        entry.Level,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                        )
                            continue;

                        if (
                            !string.IsNullOrWhiteSpace(type)
                            && !type.Split(',')
                                .Any(t =>
                                    string.Equals(
                                        t.Trim(),
                                        entry.Type,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                        )
                            continue;

                        PrintEntry(entry);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected on Ctrl+C
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync($"Stream disconnected: {ex.Message}");
                    return (int)ExitCode.ServerError;
                }

                return (int)ExitCode.Success;
            }
        );

        return command;
    }

    private static void PrintEntry(LogEntryResponse entry)
    {
        if (_lastEntryTime != DateTime.MinValue && entry.Time < _lastEntryTime)
            PrintSessionSeparator();

        _lastEntryTime = entry.Time;

        string message = CleanMessage(entry.Message);
        string timestamp = entry
            .Time.ToLocalTime()
            .ToString("d-M-yyyy HH:mm")
            .Pastel(Color.DarkGray);
        string typeName = entry.Type.ToTitleCase().PadLeft(14);

        if (!string.IsNullOrEmpty(entry.Color))
            typeName = typeName.Pastel(entry.Color);

        Console.WriteLine($"{timestamp} {typeName} | {message}");
    }

    private static void PrintSessionSeparator()
    {
        string separator = new('-', 60);
        Console.WriteLine();
        Console.WriteLine(
            $"{"",16}{"Server Restart".PadLeft(14)} | {separator}".Pastel(Color.DarkGray)
        );
        Console.WriteLine();
    }

    private static string CleanMessage(string message)
    {
        // Strip surrounding quotes from double-serialization
        if (message is ['"', _, ..] && message[^1] == '"')
            message = message[1..^1];

        // Strip ANSI escape codes
        message = AnsiEscapeRegex().Replace(message, "");

        // Unescape JSON escapes from double-serialization
        message = message
            .Replace("\\n", "\n")
            .Replace("\\r", "\r")
            .Replace("\\t", "\t")
            .Replace("\\\"", "\"")
            .Replace(@"\\", "\\");

        return message;
    }

    internal static string BuildQuery(int tail, string? level, string? type)
    {
        List<string> parts = [$"tail={tail}"];
        if (!string.IsNullOrWhiteSpace(level))
            parts.Add($"levels={Uri.EscapeDataString(level)}");
        if (!string.IsNullOrWhiteSpace(type))
            parts.Add($"types={Uri.EscapeDataString(type)}");
        return "?" + string.Join("&", parts);
    }
}
