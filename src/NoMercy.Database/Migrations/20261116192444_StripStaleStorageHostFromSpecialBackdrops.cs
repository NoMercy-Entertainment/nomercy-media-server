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
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoMercy.Database.Migrations
{
    /// <inheritdoc />
    public partial class StripStaleStorageHostFromSpecialBackdrops : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Special backdrops stored by older builds carry the retired
            // storage.nomercy.tv host, which the special DTOs stripped on every
            // read (#113). Strip it once in the data instead. SQLite's replace()
            // is case-sensitive and replaces every occurrence, exactly like the
            // string.Replace it takes over from, so clients get the same value.
            migrationBuilder.Sql(
                """
                UPDATE "Specials"
                SET "Backdrop" = replace("Backdrop", 'https://storage.nomercy.tv/laravel', '')
                WHERE instr("Backdrop", 'https://storage.nomercy.tv/laravel') > 0;
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to restore: which rows carried the host is not recorded,
            // and putting it back would only point at a retired server again.
        }
    }
}
