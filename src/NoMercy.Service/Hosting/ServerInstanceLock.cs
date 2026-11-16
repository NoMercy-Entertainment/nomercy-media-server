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
/// process ends, however it ends. A server that cannot take it knows another
/// one is running on the same data, and must not swap files under it.
/// </summary>
public sealed class ServerInstanceLock : IDisposable
{
    public const string FileName = "server.lock";

    private readonly Stream _handle;

    private ServerInstanceLock(Stream handle)
    {
        _handle = handle;
    }

    /// <summary>The lock, or null when another process holds it.</summary>
    public static ServerInstanceLock? TryAcquire(IStorageDriver storage, string dataFolder)
    {
        try
        {
            storage.CreateDirectory(dataFolder);
            return new(storage.OpenWrite(Path.Combine(dataFolder, FileName), overwrite: true));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose() => _handle.Dispose();
}
