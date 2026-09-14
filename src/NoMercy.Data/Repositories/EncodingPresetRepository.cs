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
using NoMercy.Database;
using NoMercy.Database.Models.Media;

namespace NoMercy.Data.Repositories;

/// <summary>
/// Repository for the shareable preset library. Callers (jobs, controllers)
/// hold only a preset's <see cref="Ulid"/> id; <see cref="NoMercy.Encoder.Profiles.PresetResolver"/>
/// resolves <see cref="EncodingPreset.ProfileJson"/> — walking the parent
/// chain if any — fresh each time it's needed, so an edit to a preset takes
/// effect on the next resolve rather than requiring a separate migration step.
/// </summary>
public class EncodingPresetRepository(MediaContext context) : IEncodingPresetRepository
{
    public Task<List<EncodingPreset>> ListAsync(
        int pageSize = 100,
        int pageIndex = 0,
        string? tagFilter = null
    )
    {
        if (pageSize <= 0)
            pageSize = 100;
        if (pageIndex < 0)
            pageIndex = 0;

        IQueryable<EncodingPreset> query = context.EncodingPresets.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(tagFilter))
        {
            // Tags are stored as a comma-separated string (see model). Matching
            // on substring is fine for now — the tag list is small and the
            // column isn't indexed anyway. If the preset library grows past
            // a few hundred entries we'd normalize into a join table.
            string normalized = tagFilter.Trim().ToLower();
            query = query.Where(p => p.Tags != null && p.Tags.ToLower().Contains(normalized));
        }

        return query
            .OrderBy(p => p.IsBuiltIn ? 0 : 1)
            .ThenBy(p => p.Name)
            .ThenBy(p => p.Id)
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<string>> GetAllTagsAsync()
    {
        List<string?> rows = await context
            .EncodingPresets.AsNoTracking()
            .Where(p => p.Tags != null && p.Tags != "")
            .Select(p => p.Tags)
            .ToListAsync();

        HashSet<string> tags = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? row in rows)
        {
            if (string.IsNullOrWhiteSpace(row))
                continue;

            foreach (string tag in row.Split(',', StringSplitOptions.RemoveEmptyEntries))
                tags.Add(tag.Trim());
        }

        return [.. tags.OrderBy(t => t)];
    }

    public Task<EncodingPreset?> GetByIdAsync(Ulid id) =>
        context.EncodingPresets.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);

    public Task<EncodingPreset?> GetByNameAsync(string name) =>
        context.EncodingPresets.AsNoTracking().FirstOrDefaultAsync(p => p.Name == name);

    public async Task<EncodingPreset> CreateAsync(EncodingPreset preset)
    {
        preset.CreatedAt = DateTime.UtcNow;
        preset.UpdatedAt = preset.CreatedAt;
        context.EncodingPresets.Add(preset);
        await context.SaveChangesAsync();
        return preset;
    }

    public async Task<EncodingPreset?> UpdateAsync(Ulid id, Action<EncodingPreset> apply)
    {
        EncodingPreset? existing = await context.EncodingPresets.FirstOrDefaultAsync(p =>
            p.Id == id
        );
        if (existing is null)
            return null;

        if (existing.IsBuiltIn)
            throw new InvalidOperationException(
                "Built-in presets cannot be modified in place — clone and edit instead."
            );

        apply(existing);
        existing.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAsync(Ulid id)
    {
        EncodingPreset? existing = await context.EncodingPresets.FirstOrDefaultAsync(p =>
            p.Id == id
        );
        if (existing is null)
            return false;

        if (existing.IsBuiltIn)
            throw new InvalidOperationException("Built-in presets cannot be deleted.");

        context.EncodingPresets.Remove(existing);
        await context.SaveChangesAsync();
        return true;
    }

    public Task<bool> HasChildrenAsync(Ulid id, CancellationToken ct = default) =>
        context.EncodingPresets.AsNoTracking().AnyAsync(p => p.ParentPresetId == id, ct);

    public Task<int> GetTotalCountAsync() => context.EncodingPresets.CountAsync();
}
