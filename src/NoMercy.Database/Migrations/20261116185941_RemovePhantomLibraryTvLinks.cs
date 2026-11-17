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
    public partial class RemovePhantomLibraryTvLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A show belongs to one library: Tvs.LibraryId. Before the write
            // path was fixed, re-importing a show under another library added a
            // second LibraryTv row instead of moving the link, so shows were
            // listed in a library that holds no copy of them (#105).
            //
            // A link is removed only when all of these hold, so no show loses
            // a link that is still backed by something:
            //  - it points at a library other than Tvs.LibraryId;
            //  - that library exists and the show also has its link to it, so
            //    the show keeps exactly the link the code treats as its own;
            //  - none of the show's files was found under a folder of the
            //    library being unlinked (by the folder the scan recorded in
            //    VideoFiles.Share, or by the path the file lives under).
            // A show whose only link disagrees with Tvs.LibraryId is left
            // alone, as is a show whose files sit in both libraries.
            //
            // A removed link's origin is not lost: the show's own link becomes
            // Manual when any removed link was (a show the owner added by hand
            // stays listed without a file), and keeps the earliest AddedAt of
            // them all. The rows to remove are collected once, so the fold-in
            // and the delete act on exactly the same rows. AddedAt is only
            // ever written by EF, in one sortable text format, so MIN() on
            // the text is the earliest date.
            migrationBuilder.Sql(
                """
                CREATE TEMP TABLE "PhantomLibraryTv" AS
                SELECT "LibraryId", "TvId", "AddedBy", "AddedAt"
                FROM "LibraryTv"
                WHERE EXISTS (
                    SELECT 1
                    FROM "Tvs" AS t
                    WHERE t."Id" = "LibraryTv"."TvId"
                      AND t."LibraryId" <> "LibraryTv"."LibraryId"
                      AND EXISTS (
                          SELECT 1 FROM "Libraries" AS l WHERE l."Id" = t."LibraryId"
                      )
                      AND EXISTS (
                          SELECT 1
                          FROM "LibraryTv" AS own
                          WHERE own."TvId" = t."Id"
                            AND own."LibraryId" = t."LibraryId"
                      )
                )
                AND NOT EXISTS (
                    SELECT 1
                    FROM "Episodes" AS e
                    JOIN "VideoFiles" AS v ON v."EpisodeId" = e."Id"
                    JOIN "FolderLibrary" AS fl ON fl."LibraryId" = "LibraryTv"."LibraryId"
                    JOIN "Folders" AS f ON f."Id" = fl."FolderId"
                    WHERE e."TvId" = "LibraryTv"."TvId"
                      AND (
                          v."Share" = f."Id"
                          OR substr(
                                  lower(replace(v."HostFolder", '\', '/')) || '/',
                                  1,
                                  length(rtrim(lower(replace(f."Path", '\', '/')), '/')) + 1
                              ) = rtrim(lower(replace(f."Path", '\', '/')), '/') || '/'
                      )
                );

                UPDATE "LibraryTv"
                SET "AddedBy" = CASE
                        WHEN EXISTS (
                            SELECT 1
                            FROM "PhantomLibraryTv" AS p
                            WHERE p."TvId" = "LibraryTv"."TvId" AND p."AddedBy" = 'manual'
                        ) THEN 'manual'
                        ELSE "AddedBy"
                    END,
                    "AddedAt" = coalesce(
                        min(
                            "AddedAt",
                            (SELECT min(p."AddedAt") FROM "PhantomLibraryTv" AS p WHERE p."TvId" = "LibraryTv"."TvId")
                        ),
                        "AddedAt",
                        (SELECT min(p."AddedAt") FROM "PhantomLibraryTv" AS p WHERE p."TvId" = "LibraryTv"."TvId")
                    )
                WHERE "TvId" IN (SELECT "TvId" FROM "PhantomLibraryTv")
                  AND "LibraryId" = (SELECT t."LibraryId" FROM "Tvs" AS t WHERE t."Id" = "LibraryTv"."TvId");

                DELETE FROM "LibraryTv"
                WHERE ("LibraryId", "TvId") IN (SELECT "LibraryId", "TvId" FROM "PhantomLibraryTv");

                DROP TABLE "PhantomLibraryTv";
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to restore: the removed rows were links to libraries
            // that hold no file of the show. A library scan re-creates any
            // link a file on disk still backs.
        }
    }
}
