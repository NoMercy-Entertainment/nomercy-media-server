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

using FluentAssertions;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Importing into the library, asking about metadata and dispatching a job.
/// The three things a plugin previously did by naming the server's own job
/// types and driving them with reflection, which broke silently four times in
/// three days.
/// </summary>
public class PluginLibraryImportContractTests
{
    [Fact]
    public void An_import_request_names_the_library_the_kind_and_the_file()
    {
        PluginImportRequest request = new()
        {
            Library = LibraryId.Parse("01J9ZK5V8Y0000000000000000"),
            Kind = PluginMediaKind.Episode,
            SourcePath = "staged/The.Show.S01E01.mkv",
        };

        request.Kind.Should().Be(PluginMediaKind.Episode);
        request.Program.Should().BeNull();
    }

    [Fact]
    public void A_recording_import_carries_its_guide_entry()
    {
        PluginImportRequest request = new()
        {
            Library = LibraryId.Parse("01J9ZK5V8Y0000000000000000"),
            Kind = PluginMediaKind.Recording,
            SourcePath = "recordings/npo1-2026-09-16.ts",
            Program = new PluginEpgProgram
            {
                ChannelId = "npo1",
                Title = "Journaal",
                Start = DateTimeOffset.Parse("2026-09-16T18:00:00Z"),
                Stop = DateTimeOffset.Parse("2026-09-16T18:30:00Z"),
            },
        };

        request.Program!.Title.Should().Be("Journaal");
    }

    [Fact]
    public void Import_answers_the_id_the_library_gave_the_media()
    {
        PluginImportResult result = new(MediaId.Parse("01J9ZK5V8Y0000000000000001"), null);

        result.Ok.Should().BeTrue();
        result.Media.Should().NotBe(MediaId.Empty);
    }

    [Fact]
    public void A_refused_import_answers_the_refusal_and_never_a_media_id()
    {
        PluginImportResult result = new(
            MediaId.Empty,
            PluginRefusalMessages.FileOutsideGrant("Torrent Downloader 0.4.1", "D:\\intake")
        );

        result.Ok.Should().BeFalse();
        result.Refusal!.Code.Should().Be(PluginRefusalCodes.FileOutsideGrant);
    }

    [Fact]
    public void A_metadata_query_names_the_provider_or_asks_them_all()
    {
        PluginMetadataQuery query = new()
        {
            Provider = "*",
            Kind = PluginMediaKind.Show,
            Title = "The Show",
            Year = 2019,
        };

        query.Provider.Should().Be("*");
    }

    [Fact]
    public void A_job_kind_is_a_word_the_host_knows_not_a_type_name()
    {
        PluginJobKind.Rescan.ToString().Should().Be("Rescan");
        Enum.GetNames<PluginJobKind>()
            .Should()
            .BeEquivalentTo(["Rescan", "FetchImages", "RefreshMetadata", "Encode"]);
    }

    [Fact]
    public void Dispatching_a_job_without_the_capability_refuses_and_names_the_facade()
    {
        PluginRefusal refusal = PluginRefusalMessages.CapabilityNotDeclared(
            "Torrent Downloader 0.4.1",
            PluginCapabilityNames.JobsDispatch,
            "The plugin asked the server to rescan a library."
        );

        refusal.Fix.Should().Contain("jobs.dispatch");
        refusal.Fix.Should().Contain("/nomercy-plugins/capabilities/jobs-dispatch");
    }

    [Fact]
    public void The_library_offers_import_and_watch_and_the_context_offers_metadata()
    {
        typeof(IPluginLibraryQuery)
            .GetProperty("Import")!
            .PropertyType.Should()
            .Be(typeof(IPluginLibraryImport));
        typeof(IPluginLibraryQuery)
            .GetProperty("Watch")!
            .PropertyType.Should()
            .Be(typeof(IPluginLibraryWatch));
        typeof(IPluginContext)
            .GetProperty("Metadata")!
            .PropertyType.Should()
            .Be(typeof(IPluginMetadata));
    }

    /// <summary>
    /// A recording is offered while it is still being written, so a viewer can
    /// start a program that has not finished airing. Registering only
    /// finished files would have made that impossible to add later without
    /// changing a member every plugin already calls.
    /// </summary>
    [Fact]
    public void A_library_import_can_offer_a_file_that_is_still_being_written()
    {
        typeof(IPluginLibraryImport).GetMethod("StreamAsync").Should().NotBeNull();
        typeof(IPluginLibraryImport).GetMethod("RegisterAsync").Should().NotBeNull();
    }

    /// <summary>
    /// Dispatching answers a job id, because a plugin that owns files has to
    /// read the outcome back. Inferring it from the library cannot tell a
    /// failed encode from one still running: both are "not there yet", for
    /// ever, and the plugin either waits on an episode that will never arrive
    /// or deletes a download believing its own encode finished.
    /// </summary>
    [Fact]
    public void Dispatching_answers_an_id_the_plugin_can_read_the_outcome_back_from()
    {
        typeof(IPluginJobs).GetMethod("DispatchAsync")!.ReturnType.Should().Be(typeof(Task<JobId>));
        typeof(IPluginJobs).GetMethod("StatusAsync").Should().NotBeNull();
    }

    /// <summary>
    /// The docs link in that refusal is the generated one, not a sentence
    /// somebody typed. A hand-written URL goes stale the first time a
    /// capability is renamed, and a refusal pointing at a missing page is worse
    /// than one pointing at none.
    /// </summary>
    [Fact]
    public void Every_capability_carries_the_docs_page_the_generator_emitted()
    {
        IEnumerable<PluginCapabilityDescriptor> wrong = PluginCapabilityVocabulary.All.Where(
            capability =>
                capability.DocsUrl
                != "/nomercy-plugins/capabilities/" + capability.Name.Replace('.', '-')
        );

        wrong.Should().BeEmpty();
    }
}
