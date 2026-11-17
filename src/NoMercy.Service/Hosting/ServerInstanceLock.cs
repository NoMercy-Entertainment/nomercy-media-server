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

using NoMercy.Storage;

namespace NoMercy.Service.Hosting;

/// <summary>
/// An exclusive hold on <c>server.lock</c> in the data folder, kept for the life
/// of the server process. The local driver opens the file with
/// <see cref="FileShare.None"/>, which Windows enforces and .NET maps to an
/// exclusive <c>flock</c> on Linux and macOS, and the OS releases it when the
/// process ends, however it ends. A server that cannot take it must not swap
/// files under whoever might be using them.
/// </summary>
public sealed class ServerInstanceLock : IDisposable
{
    public const string FileName = "server.lock";

    private const int Attempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(400);

    private readonly Stream _handle;

    private ServerInstanceLock(Stream handle)
    {
        _handle = handle;
    }

    /// <summary>
    /// Takes the lock. A sharing or lock violation (the file is held) is tried
    /// three times over about a second, since a virus scanner can hold a new file
    /// for a moment; any other failure (a read-only folder, a network share that
    /// cannot lock) is reported at once, as what it is.
    /// </summary>
    public static ServerInstanceLockAttempt TryAcquire(
        IStorageDriver storage,
        string dataFolder,
        Action<TimeSpan>? pause = null
    )
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                storage.CreateDirectory(dataFolder);
                Stream handle = storage.OpenWrite(
                    Path.Combine(dataFolder, FileName),
                    overwrite: true
                );
                return new(new ServerInstanceLock(handle), HeldElsewhere: false, Problem: null);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                if (attempt == Attempts)
                    return new(null, HeldElsewhere: true, exception.Message);
                (pause ?? Thread.Sleep)(RetryDelay);
            }
            catch (Exception exception)
                when (exception
                        is IOException
                            or UnauthorizedAccessException
                            or NotSupportedException
                )
            {
                return new(null, HeldElsewhere: false, exception.Message);
            }
        }
    }

    /// <summary>
    /// True for "another process has this file": ERROR_SHARING_VIOLATION or
    /// ERROR_LOCK_VIOLATION on Windows, EWOULDBLOCK from <c>flock</c> on Linux
    /// (11) and macOS (35), which .NET reports as the exception's HResult.
    /// </summary>
    internal static bool IsSharingViolation(IOException exception) =>
        OperatingSystem.IsWindows()
            ? (exception.HResult & 0xFFFF) is 32 or 33
            : exception.HResult is 11 or 35;

    public void Dispose() => _handle.Dispose();
}
