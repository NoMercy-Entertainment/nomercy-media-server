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
using System.Text;
using System.Text.Json;
using NoMercy.NmSystem.FFProbe;

namespace NoMercy.Tests.Setup.FFProbe;

[CollectionDefinition("FFprobe console encoding", DisableParallelization = true)]
public sealed class FfProbeEncodingCollection;

[Collection("FFprobe console encoding")]
[Trait("Category", "Unit")]
public sealed class FfProbeUtf8Tests
{
    [Fact]
    public async Task Utf8ProbeJsonSurvivesANonUtf8ParentConsole()
    {
        const string title = "B‐Boy · City Don’t Sleep · Beyoncé · 東京";
        string json = JsonSerializer.Serialize(
            new { format = new { tags = new { title } } },
            new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }
        );
        ProcessStartInfo startInfo = FfProbeService.CreateStartInfo("");
        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "powershell.exe";
            string bytes = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            string script =
                $"$bytes=[Convert]::FromBase64String('{bytes}'); [Console]::OpenStandardOutput().Write($bytes,0,$bytes.Length); [Console]::OpenStandardError().Write($bytes,0,$bytes.Length)";
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            string escaped = json.Replace("'", "'\"'\"'");
            startInfo.ArgumentList.Add($"printf '%s' '{escaped}'; printf '%s' '{escaped}' >&2");
        }

        Encoding original = Console.OutputEncoding;
        try
        {
            Console.OutputEncoding = Encoding.Latin1;
            using Process process = new() { StartInfo = startInfo };
            process.Start();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            using JsonDocument stdout = JsonDocument.Parse(await output);
            using JsonDocument stderr = JsonDocument.Parse(await error);
            stdout
                .RootElement.GetProperty("format")
                .GetProperty("tags")
                .GetProperty("title")
                .GetString()
                .Should()
                .Be(title);
            stderr
                .RootElement.GetProperty("format")
                .GetProperty("tags")
                .GetProperty("title")
                .GetString()
                .Should()
                .Be(title);
        }
        finally
        {
            Console.OutputEncoding = original;
        }
    }
}
