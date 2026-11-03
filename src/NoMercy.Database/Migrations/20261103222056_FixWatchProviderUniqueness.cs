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
    public partial class FixWatchProviderUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM WatchProviderMedia
                WHERE MovieId IS NOT NULL AND TvId IS NULL
                  AND rowid NOT IN (
                      SELECT MAX(rowid) FROM WatchProviderMedia
                      WHERE MovieId IS NOT NULL AND TvId IS NULL
                      GROUP BY WatchProviderId, CountryCode, ProviderType, MovieId
                  );
                DELETE FROM WatchProviderMedia
                WHERE TvId IS NOT NULL AND MovieId IS NULL
                  AND rowid NOT IN (
                      SELECT MAX(rowid) FROM WatchProviderMedia
                      WHERE TvId IS NOT NULL AND MovieId IS NULL
                      GROUP BY WatchProviderId, CountryCode, ProviderType, TvId
                  );
                """
            );

            migrationBuilder.DropIndex(
                name: "IX_WatchProviderMedia_WatchProviderId_CountryCode_ProviderType_MovieId_TvId",
                table: "WatchProviderMedia"
            );

            migrationBuilder.CreateIndex(
                name: "IX_WatchProviderMedia_WatchProviderId_CountryCode_ProviderType_MovieId",
                table: "WatchProviderMedia",
                columns: new[] { "WatchProviderId", "CountryCode", "ProviderType", "MovieId" },
                unique: true,
                filter: "MovieId IS NOT NULL AND TvId IS NULL"
            );

            migrationBuilder.CreateIndex(
                name: "IX_WatchProviderMedia_WatchProviderId_CountryCode_ProviderType_TvId",
                table: "WatchProviderMedia",
                columns: new[] { "WatchProviderId", "CountryCode", "ProviderType", "TvId" },
                unique: true,
                filter: "TvId IS NOT NULL AND MovieId IS NULL"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WatchProviderMedia_WatchProviderId_CountryCode_ProviderType_MovieId",
                table: "WatchProviderMedia"
            );

            migrationBuilder.DropIndex(
                name: "IX_WatchProviderMedia_WatchProviderId_CountryCode_ProviderType_TvId",
                table: "WatchProviderMedia"
            );

            migrationBuilder.CreateIndex(
                name: "IX_WatchProviderMedia_WatchProviderId_CountryCode_ProviderType_MovieId_TvId",
                table: "WatchProviderMedia",
                columns: new[]
                {
                    "WatchProviderId",
                    "CountryCode",
                    "ProviderType",
                    "MovieId",
                    "TvId",
                },
                unique: true
            );
        }
    }
}
