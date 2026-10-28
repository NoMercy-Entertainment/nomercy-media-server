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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace NoMercy.Database.Maintenance;

public static class RestrictedChildren
{
    private const int BatchSize = 500;

    /// <summary>
    /// Deletes the rows that a RESTRICT foreign key would otherwise use to block
    /// deleting the given parents. Reads the dependents from the EF model rather
    /// than a hand-kept list, so a child table added later is covered the day it
    /// is mapped instead of resurfacing this as a boot-time failure.
    /// </summary>
    public static async Task ClearAsync(
        MediaContext mediaContext,
        Type parentType,
        List<Guid> parentIds,
        CancellationToken cancellationToken
    )
    {
        if (parentIds.Count == 0)
            return;

        IEntityType parent =
            mediaContext.Model.FindEntityType(parentType)
            ?? throw new InvalidOperationException($"{parentType.Name} is not a mapped entity.");

        foreach (Guid[] batch in parentIds.Chunk(BatchSize))
        {
            string placeholders = string.Join(
                ",",
                Enumerable.Range(0, batch.Length).Select(index => $"{{{index}}}")
            );
            object[] parameters = batch.Cast<object>().ToArray();

            foreach (IForeignKey foreignKey in parent.GetReferencingForeignKeys())
            {
                if (foreignKey.DeleteBehavior != DeleteBehavior.Restrict)
                    continue;

                IEntityType dependent = foreignKey.DeclaringEntityType;
                string? table = dependent.GetTableName();
                if (table is null)
                    continue;

                StoreObjectIdentifier storeObject = StoreObjectIdentifier.Table(
                    table,
                    dependent.GetSchema()
                );
                string? column = foreignKey.Properties[0].GetColumnName(storeObject);
                if (column is null)
                    continue;

                // Table and column are EF model metadata, never user input; the ids
                // are bound as parameters.
                string sql = string.Concat(
                    "DELETE FROM \"",
                    table,
                    "\" WHERE \"",
                    column,
                    "\" IN (",
                    placeholders,
                    ")"
                );

                await mediaContext.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
            }
        }
    }
}
