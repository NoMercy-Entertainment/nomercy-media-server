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
using System.Diagnostics;
using Newtonsoft.Json;
using NoMercy.Cli.Models;
using NoMercy.NmSystem.FileSystem;
using NoMercy.NmSystem.Information;

namespace NoMercy.Cli.Commands;

internal static class UpdateCommand
{
    /// <summary>
    /// <paramref name="startServer"/> and <paramref name="awaitVersion"/> are the two steps
    /// that reach outside the process — launching the updated binary and waiting for it to
    /// answer. They are injectable so the swap and rollback logic can be tested without
    /// spawning a real server or waiting out a two-minute poll.
    /// </summary>
    public static Command Create(
        Option<string?> pipeOption,
        ICliClientFactory clientFactory,
        Func<string, bool>? startServer = null,
        Func<ICliClient, CancellationToken, Task<string?>>? awaitVersion = null,
        Func<ICliClient, CancellationToken, Task<bool>>? awaitExit = null,
        Action<string, string>? moveStagedBinary = null
    )
    {
        startServer ??= StartServer;
        moveStagedBinary ??= File.Move;
        awaitVersion ??= (client, ct) =>
            WaitForServerVersionAsync(client, TimeSpan.FromMinutes(2), ct);

        // Detecting "the server has exited" depends on the management transport failing to
        // connect, which a named pipe and a Unix socket signal differently. Injectable so the
        // swap and rollback logic can be tested without depending on that difference.
        awaitExit ??= (client, ct) => WaitForServerExitAsync(client, TimeSpan.FromSeconds(60), ct);

        Command command = new("update") { Description = "Download and stage a server update" };

        command.SetAction(
            async (parseResult, ct) =>
            {
                string? pipe = parseResult.GetValue(pipeOption);
                using ICliClient client = clientFactory.Create(pipe);

                // Step 1: Trigger download
                Console.WriteLine("Downloading update...");
                UpdateResponse? downloadResponse = await client.PostAsync<UpdateResponse>(
                    ApiRoutes.Update,
                    null,
                    ct
                );

                if (downloadResponse is null || downloadResponse.Status != "ok")
                {
                    await Console.Error.WriteLineAsync(
                        downloadResponse?.Message ?? "Failed to download update."
                    );
                    return (int)ExitCode.ServerError;
                }

                Console.WriteLine(downloadResponse.Message);

                if (
                    downloadResponse.Message.Contains(
                        "already up to date",
                        StringComparison.OrdinalIgnoreCase
                    )
                    || downloadResponse.Message.Contains(
                        "restart needed",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    return (int)ExitCode.Success;

                // Deployments where a binary swap is not the update mechanism. Saying so and
                // stopping is the correct outcome; carrying on would stage a file that can
                // never run, and in a container it would also take the server down with it.
                if (downloadResponse.UseContainerImage)
                {
                    Console.WriteLine(
                        "Pull the new container image to apply this update, then recreate the container."
                    );
                    return (int)ExitCode.Success;
                }

                if (downloadResponse.UseInstaller)
                {
                    Console.WriteLine("Run the installer to apply this update.");
                    return (int)ExitCode.Success;
                }

                string tempPath = AppFiles.ServerTempExePath;
                string currentPath = AppFiles.ServerExePath;

                // Verified before the running server is touched. Stopping a healthy server for
                // an update that turns out not to be staged is a self-inflicted outage.
                if (!File.Exists(tempPath))
                {
                    await Console.Error.WriteLineAsync(
                        "No staged update file found — the server is still running and untouched."
                    );
                    return (int)ExitCode.ServerError;
                }

                // Step 2: Stop the server
                Console.WriteLine("Stopping server...");
                bool stopped = await client.PostAsync(ApiRoutes.Stop, null, ct);
                if (!stopped)
                {
                    await Console.Error.WriteLineAsync("Failed to send stop command.");
                    return (int)ExitCode.ServerError;
                }

                // Step 3: Wait for exit
                Console.WriteLine("Waiting for server to exit...");
                bool exited = await awaitExit(client, ct);
                if (!exited)
                {
                    // Swapping under a live process is how an update half-applies: on Windows
                    // the file is locked and the move throws, on Linux it succeeds while the old
                    // binary keeps running, so which version comes back is down to timing.
                    await Console.Error.WriteLineAsync(
                        "The server did not stop within 60s. Nothing was changed — stop it and run "
                            + "'nomercy update' again."
                    );
                    return (int)ExitCode.Timeout;
                }

                // Step 4: Apply the swap, keeping the old binary until the new one is proven
                string backupPath = currentPath + ".previous";

                try
                {
                    if (File.Exists(backupPath))
                        File.Delete(backupPath);

                    // Move, never delete: until the replacement is in place and starts, the
                    // version that was working is the only thing standing between the user and
                    // a server that no longer exists.
                    if (File.Exists(currentPath))
                        File.Move(currentPath, backupPath);

                    moveStagedBinary(tempPath, currentPath);
                    await FilePermissions.SetExecutionPermissions(currentPath);
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync($"Could not apply the update: {ex.Message}");
                    RestoreBackup(backupPath, currentPath);
                    if (!startServer(currentPath))
                    {
                        await Console.Error.WriteLineAsync(
                            "Could not restart the previous server after rollback. The server is down."
                        );
                        return (int)ExitCode.RollbackFailed;
                    }

                    Console.WriteLine("Previous version restored and started.");
                    return (int)ExitCode.ServerError;
                }

                Console.WriteLine("Update applied. Starting server...");

                if (!startServer(currentPath))
                {
                    await Console.Error.WriteLineAsync(
                        "The updated server would not start — rolling back to the previous version."
                    );
                    RestoreBackup(backupPath, currentPath);
                    if (!startServer(currentPath))
                    {
                        await Console.Error.WriteLineAsync(
                            "Could not restart the previous server after rollback. The server is down."
                        );
                        return (int)ExitCode.RollbackFailed;
                    }

                    Console.WriteLine("Previous version restored and started.");
                    return (int)ExitCode.ServerError;
                }

                // "It said it worked" is not the same as "it is running the new version", and
                // the difference is the entire complaint about updates.
                string? runningVersion = await awaitVersion(client, ct);

                if (runningVersion is null)
                {
                    await Console.Error.WriteLineAsync(
                        "The updated server did not come back — rolling back."
                    );
                    RestoreBackup(backupPath, currentPath);
                    if (!startServer(currentPath))
                    {
                        await Console.Error.WriteLineAsync(
                            "Could not restart the previous server after rollback. The server is down."
                        );
                        return (int)ExitCode.RollbackFailed;
                    }

                    Console.WriteLine("Previous version restored and started.");
                    return (int)ExitCode.ServerError;
                }

                File.Delete(backupPath);
                Console.WriteLine($"Server is running version {runningVersion}.");
                return (int)ExitCode.Success;
            }
        );

        return command;
    }

    private static async Task<bool> WaitForServerExitAsync(
        ICliClient client,
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        while (true)
        {
            if (await HasServerStoppedRespondingAsync(client, cts.Token))
            {
                return true;
            }

            try
            {
                await Task.Delay(500, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Re-throw a genuine caller cancellation; a fired timeout simply
                // means the server never confirmed that it stopped.
                ct.ThrowIfCancellationRequested();
                return false;
            }
        }
    }

    // Returns true only when the server's management endpoint is provably
    // unreachable (connection refused / pipe closed), which means the process
    // has exited and the file swap is safe. Any successful or merely
    // unsuccessful HTTP response is treated as "still running" so a transient
    // error can never trigger a premature swap.
    private static async Task<bool> HasServerStoppedRespondingAsync(
        ICliClient client,
        CancellationToken ct
    )
    {
        try
        {
            await client.GetRawAsync(ApiRoutes.Status, ct);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Puts the previous binary back after a failed swap or a failed start. Without it a
    /// half-applied update leaves no server binary at all and no way back.
    /// </summary>
    private static void RestoreBackup(string backupPath, string currentPath)
    {
        try
        {
            if (!File.Exists(backupPath))
                return;

            if (File.Exists(currentPath))
                File.Delete(currentPath);

            File.Move(backupPath, currentPath);
            Console.WriteLine("Previous version restored.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Could not restore the previous version from {backupPath}: {ex.Message}"
            );
        }
    }

    private static bool StartServer(string executablePath)
    {
        try
        {
            ProcessStartInfo startInfo = new(executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
            };

            return Process.Start(startInfo) is not null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not start the server: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Polls until the server answers with a version, which is the only proof the update
    /// actually took effect.
    /// </summary>
    private static async Task<string?> WaitForServerVersionAsync(
        ICliClient client,
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        while (!cts.IsCancellationRequested)
        {
            try
            {
                StatusResponse? status = await client.GetAsync<StatusResponse>(
                    ApiRoutes.Status,
                    cts.Token
                );

                if (!string.IsNullOrEmpty(status?.Version))
                    return status.Version;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Still starting.
            }

            try
            {
                await Task.Delay(2000, cts.Token);
            }
            catch (OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                return null;
            }
        }

        return null;
    }

    private class UpdateResponse
    {
        [JsonProperty("status")]
        public string Status { get; set; } = string.Empty;

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        [JsonProperty("use_installer")]
        public bool UseInstaller { get; set; }

        [JsonProperty("use_container_image")]
        public bool UseContainerImage { get; set; }
    }
}
