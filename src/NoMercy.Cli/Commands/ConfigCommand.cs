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
using System.Globalization;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using NoMercy.Cli.Models;

namespace NoMercy.Cli.Commands;

internal static class ConfigCommand
{
    private static readonly JsonSerializerSettings OmitUnsetKeys = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
    };

    // `config set` accepts exactly the keys the server reads on PUT, so the list is
    // read off ConfigUpdateRequest instead of being kept by hand next to it.
    private static readonly IReadOnlyDictionary<string, PropertyInfo> WritableKeys =
        typeof(ConfigUpdateRequest)
            .GetProperties()
            .Select(property => (Key: JsonKey(property), Property: property))
            .Where(entry => entry.Key is not null)
            .ToDictionary(entry => entry.Key!, entry => entry.Property);

    public static Command Create(Option<string?> pipeOption, ICliClientFactory clientFactory)
    {
        Command getCmd = new("get") { Description = "Show current configuration" };

        getCmd.SetAction(
            async (parseResult, ct) =>
            {
                string? pipe = parseResult.GetValue(pipeOption);
                using ICliClient client = clientFactory.Create(pipe);
                ConfigResponse? config = await client.GetAsync<ConfigResponse>(
                    ApiRoutes.Config,
                    ct
                );

                if (config is null)
                {
                    await Console.Error.WriteLineAsync("Could not connect to server.");
                    return (int)ExitCode.ServerError;
                }

                Console.WriteLine($"Server Name:      {config.ServerName}");
                Console.WriteLine($"Internal Port:    {config.InternalPort}");
                Console.WriteLine($"External Port:    {config.ExternalPort}");
                Console.WriteLine($"Library Workers:  {config.LibraryWorkers}");
                Console.WriteLine($"Import Workers:   {config.ImportWorkers}");
                Console.WriteLine($"Extras Workers:   {config.ExtrasWorkers}");
                Console.WriteLine($"Encoder Workers:  {config.EncoderWorkers}");
                Console.WriteLine($"Cron Workers:     {config.CronWorkers}");
                Console.WriteLine($"Image Workers:    {config.ImageWorkers}");
                Console.WriteLine($"File Workers:     {config.FileWorkers}");
                Console.WriteLine($"Music Workers:    {config.MusicWorkers}");
                Console.WriteLine($"Swagger:          {config.Swagger}");
                return (int)ExitCode.Success;
            }
        );

        Argument<string> keyArg = new("key")
        {
            Description =
                "Configuration key to set. One of: " + string.Join(", ", WritableKeys.Keys),
        };
        Argument<string> valArg = new("value") { Description = "Value to set" };

        Command setCmd = new("set") { Description = "Update a configuration value" };
        setCmd.Arguments.Add(keyArg);
        setCmd.Arguments.Add(valArg);

        setCmd.SetAction(
            async (parseResult, ct) =>
            {
                string? pipe = parseResult.GetValue(pipeOption);
                string key = parseResult.GetValue(keyArg)!;
                string val = parseResult.GetValue(valArg)!;

                if (!WritableKeys.TryGetValue(ToSnakeCase(key), out PropertyInfo? property))
                {
                    await Console.Error.WriteLineAsync($"Unknown configuration key: {key}");
                    await Console.Error.WriteLineAsync(
                        $"Accepted keys: {string.Join(", ", WritableKeys.Keys)}"
                    );
                    return (int)ExitCode.ConfigurationError;
                }

                if (!TryConvert(val, property.PropertyType, out object? converted))
                {
                    await Console.Error.WriteLineAsync(
                        $"Invalid value for {key}: expected {ValueKind(property.PropertyType)}, got '{val}'"
                    );
                    return (int)ExitCode.ConfigurationError;
                }

                ConfigUpdateRequest request = new();
                property.SetValue(request, converted);
                string json = JsonConvert.SerializeObject(request, OmitUnsetKeys);
                StringContent content = new(json, Encoding.UTF8, "application/json");

                using ICliClient client = clientFactory.Create(pipe);
                bool ok = await client.PutAsync(ApiRoutes.Config, content, ct);

                if (ok)
                {
                    Console.WriteLine($"Configuration updated: {key} = {val}");
                    return (int)ExitCode.Success;
                }

                return (int)ExitCode.ServerError;
            }
        );

        Command command = new("config") { Description = "Manage server configuration" };
        command.Subcommands.Add(getCmd);
        command.Subcommands.Add(setCmd);

        return command;
    }

    internal static string ToSnakeCase(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        StringBuilder sb = new();
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c == '-' || c == '_')
            {
                sb.Append('_');
                continue;
            }
            if (char.IsUpper(c) && i > 0 && input[i - 1] != '_' && input[i - 1] != '-')
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static string? JsonKey(PropertyInfo property) =>
        property.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName;

    private static bool TryConvert(string value, Type targetType, out object? converted)
    {
        Type type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        try
        {
            converted = Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex)
            when (ex is FormatException or OverflowException or InvalidCastException)
        {
            converted = null;
            return false;
        }
    }

    private static string ValueKind(Type targetType) =>
        (Nullable.GetUnderlyingType(targetType) ?? targetType) == typeof(int)
            ? "a whole number"
            : "text";
}
