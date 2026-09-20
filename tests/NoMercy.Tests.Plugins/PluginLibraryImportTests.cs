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
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Library;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A plugin that downloaded something used to write into a library folder and
/// hope the scanner noticed. Registering says the file is finished, so nothing
/// is filed halfway through a copy.
/// </summary>
public class PluginLibraryImportTests
{
    private static readonly Ulid Torrent = Ulid.Parse("01J9ZK5V8Y0000000000000004");
    private const string Granted = "01J9ZK5V8Y0000000000000009";
    private const string Ungranted = "01J9ZK5V8Y000000000000000A";

    private static (PluginLibraryImport Import, RecordingScanner Scanner) Build(
        bool canWrite = true,
        params string[] writable
    )
    {
        RecordingScanner scanner = new();

        return (
            new(
                Torrent,
                new StubWriter(canWrite, writable.Length == 0 ? [Granted] : writable),
                scanner,
                NullLogger.Instance
            ),
            scanner
        );
    }

    private static PluginImportRequest Request(
        string path = "/media/Downloads/show.s01e01.mkv",
        string? library = null
    ) =>
        new()
        {
            Library = new(Ulid.Parse(library ?? Granted)),
            Kind = PluginMediaKind.Episode,
            SourcePath = path,
        };

    [Fact]
    public async Task A_finished_file_in_a_granted_library_is_filed()
    {
        (PluginLibraryImport import, RecordingScanner scanner) = Build();

        PluginImportResult result = await import.RegisterAsync(Request());

        result.Ok.Should().BeTrue();
        scanner.Imported.Should().ContainSingle();
        scanner.Streaming.Should().BeFalse();
    }

    [Fact]
    public async Task A_file_the_plugin_may_not_write_to_is_refused_and_nothing_is_filed()
    {
        (PluginLibraryImport import, RecordingScanner scanner) = Build(canWrite: false);

        PluginImportResult result = await import.RegisterAsync(Request());

        result.Ok.Should().BeFalse();
        result.Refusal!.Code.Should().Be(PluginRefusalCodes.LibraryImportDenied);
        scanner
            .Imported.Should()
            .BeEmpty("a grant names a library, not a starting point to walk out of");
    }

    [Fact]
    public async Task A_library_the_plugin_holds_no_grant_for_is_refused()
    {
        (PluginLibraryImport import, RecordingScanner scanner) = Build();

        PluginImportResult result = await import.RegisterAsync(Request(library: Ungranted));

        result.Refusal!.Code.Should().Be(PluginRefusalCodes.LibraryImportDenied);
        result.Refusal.Why.Should().Contain(Ungranted);
        scanner.Imported.Should().BeEmpty();
    }

    [Fact]
    public async Task Writing_somewhere_granted_does_not_open_every_library()
    {
        (PluginLibraryImport import, _) = Build(canWrite: true, writable: Ungranted);

        PluginImportResult result = await import.RegisterAsync(Request(library: Granted));

        result
            .Ok.Should()
            .BeFalse(
                "the path check and the library check answer different questions and both have to pass"
            );
    }

    [Fact]
    public async Task A_request_naming_no_file_is_refused_before_anything_is_asked()
    {
        (PluginLibraryImport import, RecordingScanner scanner) = Build();

        PluginImportResult result = await import.RegisterAsync(Request(path: "   "));

        result.Refusal!.Why.Should().Contain("named no file");
        scanner.Imported.Should().BeEmpty();
    }

    [Fact]
    public async Task A_recording_still_being_written_is_filed_as_streaming()
    {
        (PluginLibraryImport import, RecordingScanner scanner) = Build();

        PluginImportResult result = await import.StreamAsync(Request());

        result.Ok.Should().BeTrue();
        scanner
            .Streaming.Should()
            .BeTrue("scanning it now would measure a length that is still growing");
    }

    [Fact]
    public async Task What_the_plugin_said_about_the_file_reaches_the_server()
    {
        (PluginLibraryImport import, RecordingScanner scanner) = Build();

        await import.RegisterAsync(Request() with { Title = "The Show", Year = 2026, Move = true });

        PluginImportRequest filed = scanner.Imported.Should().ContainSingle().Subject;
        filed.Title.Should().Be("The Show");
        filed.Year.Should().Be(2026);
        filed.Move.Should().BeTrue("a torrent still seeding must not be moved out from under it");
    }

    private sealed class StubWriter(bool canWrite, string[] writable) : IPluginLibraryWriter
    {
        public Task<IReadOnlyList<PluginLibrary>> GetWritableLibrariesAsync(
            CancellationToken ct = default
        ) =>
            Task.FromResult<IReadOnlyList<PluginLibrary>>([
                .. writable.Select(id => new PluginLibrary(id, "Library", "movie")),
            ]);

        public Task<bool> CanWriteAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(canWrite);

        public Task RecycleAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

        public Task MoveAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken ct = default
        ) => Task.CompletedTask;
    }

    private sealed class RecordingScanner : IPluginLibraryScanner
    {
        public List<PluginImportRequest> Imported { get; } = [];

        public bool Streaming { get; private set; }

        public Task<MediaId> ImportAsync(
            PluginImportRequest request,
            bool streaming,
            CancellationToken ct = default
        )
        {
            Imported.Add(request);
            Streaming = streaming;

            return Task.FromResult<MediaId>(default);
        }
    }
}
