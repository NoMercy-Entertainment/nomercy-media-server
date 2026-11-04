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
            // Rebuild instead of DELETE: a live install holds millions of duplicates.
            migrationBuilder.Sql(
                """
                CREATE TABLE "WatchProviderMedia_new" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_WatchProviderMedia" PRIMARY KEY,
                    "CountryCode" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL DEFAULT (CURRENT_TIMESTAMP),
                    "Link" TEXT NULL,
                    "MovieId" INTEGER NULL,
                    "ProviderType" TEXT NOT NULL,
                    "TvId" INTEGER NULL,
                    "UpdatedAt" TEXT NOT NULL DEFAULT (CURRENT_TIMESTAMP),
                    "WatchProviderId" INTEGER NOT NULL,
                    CONSTRAINT "FK_WatchProviderMedia_Movies_MovieId" FOREIGN KEY ("MovieId") REFERENCES "Movies" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_WatchProviderMedia_Tvs_TvId" FOREIGN KEY ("TvId") REFERENCES "Tvs" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_WatchProviderMedia_WatchProviders_WatchProviderId" FOREIGN KEY ("WatchProviderId") REFERENCES "WatchProviders" ("Id") ON DELETE RESTRICT
                );
                INSERT INTO "WatchProviderMedia_new"
                    (Id, CountryCode, CreatedAt, Link, MovieId, ProviderType, TvId, UpdatedAt, WatchProviderId)
                SELECT Id, CountryCode, CreatedAt, Link, MovieId, ProviderType, TvId, UpdatedAt, WatchProviderId
                FROM WatchProviderMedia
                WHERE rowid IN (
                    SELECT MAX(rowid) FROM WatchProviderMedia
                    WHERE MovieId IS NOT NULL AND TvId IS NULL
                    GROUP BY WatchProviderId, CountryCode, ProviderType, MovieId
                );
                INSERT INTO "WatchProviderMedia_new"
                    (Id, CountryCode, CreatedAt, Link, MovieId, ProviderType, TvId, UpdatedAt, WatchProviderId)
                SELECT Id, CountryCode, CreatedAt, Link, MovieId, ProviderType, TvId, UpdatedAt, WatchProviderId
                FROM WatchProviderMedia
                WHERE rowid IN (
                    SELECT MAX(rowid) FROM WatchProviderMedia
                    WHERE TvId IS NOT NULL AND MovieId IS NULL
                    GROUP BY WatchProviderId, CountryCode, ProviderType, TvId
                );
                INSERT INTO "WatchProviderMedia_new"
                    (Id, CountryCode, CreatedAt, Link, MovieId, ProviderType, TvId, UpdatedAt, WatchProviderId)
                SELECT Id, CountryCode, CreatedAt, Link, MovieId, ProviderType, TvId, UpdatedAt, WatchProviderId
                FROM WatchProviderMedia
                WHERE (MovieId IS NULL) = (TvId IS NULL);
                DROP TABLE "WatchProviderMedia";
                ALTER TABLE "WatchProviderMedia_new" RENAME TO "WatchProviderMedia";
                """
            );

            migrationBuilder.CreateIndex(
                name: "IX_WatchProviderMedia_MovieId",
                table: "WatchProviderMedia",
                column: "MovieId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_WatchProviderMedia_TvId",
                table: "WatchProviderMedia",
                column: "TvId"
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
