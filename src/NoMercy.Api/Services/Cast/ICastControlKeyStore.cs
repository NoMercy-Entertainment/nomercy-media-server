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

using NoMercy.Database;
using NoMercy.Database.Models.Common;

namespace NoMercy.Api.Services.Cast;

/// <summary>Where the per-server control-token signing key is kept between runs.</summary>
public interface ICastControlKeyStore
{
    string? Load();
    void Save(string privateKeyPem);
}

/// <summary>
/// Keeps the key as a protected <c>SecureValue</c> row, the same way the server keeps
/// its TLS private key.
/// </summary>
public sealed class CastControlKeyStore : ICastControlKeyStore
{
    private const string ConfigKey = "cast_control_signing_key";

    public string? Load()
    {
        using AppDbContext db = new();
        return db
            .Configuration.Where(c => c.Key == ConfigKey)
            .Select(c => c.SecureValue)
            .FirstOrDefault();
    }

    public void Save(string privateKeyPem)
    {
        using AppDbContext db = new();
        Configuration? existing = db.Configuration.FirstOrDefault(c => c.Key == ConfigKey);
        if (existing is null)
            db.Configuration.Add(new() { Key = ConfigKey, SecureValue = privateKeyPem });
        else
            existing.SecureValue = privateKeyPem;
        db.SaveChanges();
    }
}
