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

using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Tests.Repositories.Infrastructure;

namespace NoMercy.Tests.Repositories;

[Trait("Category", "Characterization")]
public class EncodingPresetRepositoryTests : IDisposable
{
    private readonly MediaContext _context;
    private readonly EncodingPresetRepository _repository;

    public EncodingPresetRepositoryTests()
    {
        _context = TestMediaContextFactory.CreateSeededContext();
        _repository = new(_context);
    }

    [Fact]
    public async Task CreateAsync_PersistsPresetAndAssignsTimestamps()
    {
        EncodingPreset preset = Build(name: "General 1080p");

        EncodingPreset saved = await _repository.CreateAsync(preset);

        Assert.NotEqual(default, saved.CreatedAt);
        Assert.Equal(saved.CreatedAt, saved.UpdatedAt);

        EncodingPreset? loaded = await _repository.GetByIdAsync(saved.Id);
        Assert.NotNull(loaded);
        Assert.Equal("General 1080p", loaded!.Name);
    }

    [Fact]
    public async Task GetByNameAsync_ReturnsExactMatch()
    {
        await _repository.CreateAsync(Build(name: "Anime"));
        await _repository.CreateAsync(Build(name: "Archive"));

        EncodingPreset? anime = await _repository.GetByNameAsync("Anime");

        Assert.NotNull(anime);
        Assert.Equal("Anime", anime!.Name);
    }

    [Fact]
    public async Task ListAsync_PutsBuiltInsFirst()
    {
        await _repository.CreateAsync(Build(name: "zzz-user", isBuiltIn: false));
        await _repository.CreateAsync(Build(name: "aaa-builtin", isBuiltIn: true));

        // Seeded baseline data (e.g. the shared "Default HLS" preset) may add
        // other non-builtin rows to the page, so assert relative order between
        // the two rows this test controls rather than absolute page positions.
        List<EncodingPreset> page = await _repository.ListAsync();

        int builtinIndex = page.FindIndex(p => p.Name == "aaa-builtin");
        int userIndex = page.FindIndex(p => p.Name == "zzz-user");

        Assert.True(builtinIndex >= 0, "aaa-builtin should be present in the page");
        Assert.True(userIndex >= 0, "zzz-user should be present in the page");
        Assert.True(builtinIndex < userIndex, "built-in presets must sort before user presets");
    }

    [Fact]
    public async Task UpdateAsync_AppliesChanges_AndRefreshesTimestamp()
    {
        EncodingPreset original = await _repository.CreateAsync(
            Build(name: "V1", description: "before")
        );

        await Task.Delay(5); // Ensure UtcNow moves forward.
        EncodingPreset? updated = await _repository.UpdateAsync(
            original.Id,
            p => p.Description = "after"
        );

        Assert.NotNull(updated);
        Assert.Equal("after", updated!.Description);
        Assert.True(updated.UpdatedAt >= original.UpdatedAt);
    }

    [Fact]
    public async Task UpdateAsync_BuiltInPreset_Throws()
    {
        EncodingPreset builtin = await _repository.CreateAsync(Build(isBuiltIn: true));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.UpdateAsync(builtin.Id, p => p.Description = "nope")
        );
    }

    [Fact]
    public async Task DeleteAsync_BuiltInPreset_Throws()
    {
        EncodingPreset builtin = await _repository.CreateAsync(Build(isBuiltIn: true));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.DeleteAsync(builtin.Id)
        );
    }

    [Fact]
    public async Task DeleteAsync_UserPreset_Removes()
    {
        EncodingPreset user = await _repository.CreateAsync(Build(isBuiltIn: false));

        bool removed = await _repository.DeleteAsync(user.Id);

        Assert.True(removed);
        Assert.Null(await _repository.GetByIdAsync(user.Id));
    }

    [Fact]
    public async Task DeleteAsync_UnknownId_ReturnsFalse()
    {
        bool removed = await _repository.DeleteAsync(Ulid.NewUlid());
        Assert.False(removed);
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_ReturnsNull()
    {
        EncodingPreset? updated = await _repository.UpdateAsync(Ulid.NewUlid(), _ => { });
        Assert.Null(updated);
    }

    [Fact]
    public async Task GetTotalCountAsync_CountsEveryRow()
    {
        int before = await _repository.GetTotalCountAsync();

        await _repository.CreateAsync(Build(name: "a"));
        await _repository.CreateAsync(Build(name: "b"));
        await _repository.CreateAsync(Build(name: "c"));

        int count = await _repository.GetTotalCountAsync();

        Assert.Equal(before + 3, count);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ListAsync_TagFilter_ReturnsOnlyMatches()
    {
        await _repository.CreateAsync(Build(name: "Anime 1080p", tags: "anime,1080p,h264"));
        await _repository.CreateAsync(Build(name: "Archive", tags: "archival,h265"));
        await _repository.CreateAsync(Build(name: "Web", tags: "web,720p"));

        List<EncodingPreset> animeMatches = await _repository.ListAsync(tagFilter: "anime");
        List<EncodingPreset> hevcMatches = await _repository.ListAsync(tagFilter: "h265");

        Assert.Single(animeMatches);
        Assert.Equal("Anime 1080p", animeMatches[0].Name);
        Assert.Single(hevcMatches);
        Assert.Equal("Archive", hevcMatches[0].Name);
    }

    [Fact]
    public async Task ListAsync_TagFilter_IsCaseInsensitive()
    {
        await _repository.CreateAsync(Build(name: "a", tags: "Anime,Drama"));

        List<EncodingPreset> lower = await _repository.ListAsync(tagFilter: "anime");
        List<EncodingPreset> upper = await _repository.ListAsync(tagFilter: "ANIME");

        Assert.Single(lower);
        Assert.Single(upper);
    }

    [Fact]
    public async Task ListAsync_TagFilter_UnknownTagReturnsEmpty()
    {
        await _repository.CreateAsync(Build(tags: "anime"));

        List<EncodingPreset> matches = await _repository.ListAsync(tagFilter: "nonexistent");

        Assert.Empty(matches);
    }

    [Fact]
    public async Task GetAllTagsAsync_ReturnsDistinctSortedTags()
    {
        await _repository.CreateAsync(Build(name: "a", tags: "anime,1080p"));
        await _repository.CreateAsync(Build(name: "b", tags: "anime,archival"));
        await _repository.CreateAsync(Build(name: "c", tags: null));

        IReadOnlyList<string> tags = await _repository.GetAllTagsAsync();

        Assert.Equal(["1080p", "anime", "archival"], tags);
    }

    [Fact]
    public async Task HasChildrenAsync_IsTrueOnlyForAPresetSomethingInheritsFrom()
    {
        EncodingPreset parent = await _repository.CreateAsync(Build(name: "Parent"));
        EncodingPreset child = Build(name: "Child");
        child.ParentPresetId = parent.Id;
        await _repository.CreateAsync(child);

        Assert.True(await _repository.HasChildrenAsync(parent.Id));
        Assert.False(await _repository.HasChildrenAsync(child.Id));
    }

    private static EncodingPreset Build(
        string name = "Sample",
        string? description = null,
        string? tags = null,
        bool isBuiltIn = false
    ) =>
        new()
        {
            Name = name,
            Description = description,
            Tags = tags,
            ProfileJson =
                "{\"Name\":\"sample\",\"Format\":\"Hls\",\"VideoOutputs\":[],\"AudioOutputs\":[],\"SubtitleOutputs\":[]}",
            IsBuiltIn = isBuiltIn,
        };
}
