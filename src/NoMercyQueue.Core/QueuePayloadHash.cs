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

using System.Security.Cryptography;
using System.Text;

namespace NoMercyQueue.Core;

/// <summary>
/// The fixed-width stand-in a queue row carries so enqueue can ask "is this exact
/// job already queued?" without indexing the payload itself.
/// <para>
/// Dedup compares whole payloads, and an index over that column is an index over
/// the payloads: a music encode payload runs to a megabyte, so the index grew to
/// roughly the size of the table it indexed and doubled the database. Hashing
/// gives the lookup a 64-byte key while the caller still confirms against the
/// real payload, so a collision cannot silently swallow a job.
/// </para>
/// </summary>
public static class QueuePayloadHash
{
    public const int Length = 64;

    public static string For(string payload)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>The original hash when set, else the hash of the payload.</summary>
    public static string Original(string original, string payload)
    {
        return string.IsNullOrEmpty(original) ? For(payload) : original;
    }
}
