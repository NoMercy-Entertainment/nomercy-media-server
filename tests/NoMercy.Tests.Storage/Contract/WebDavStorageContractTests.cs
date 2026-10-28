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

using System.Net;
using NoMercy.Storage.Remote;
using NoMercy.Tests.Storage.Container;

namespace NoMercy.Tests.Storage.Contract;

/// <summary>
/// Runs the shared IStorage contract suite against the shared Apache WebDAV backend
/// in the all-in-one StorageBackends container.
///
/// Design:
///   - <see cref="StorageBackendsFixture"/> starts one container for the whole assembly.
///   - Each test in the base class calls <see cref="CreateStorage"/> then
///     <see cref="DisposeStorage"/> in its own try/finally.
///   - <see cref="CreateStorage"/> calls <c>Skip.If(!fixture.Available)</c> so every
///     inherited [Fact] skips cleanly when Docker is absent.
///   - Seed helpers use raw HTTP (MKCOL / PUT) and PROPFIND to verify, bypassing
///     the driver under test.
/// </summary>
[Collection("StorageBackends")]
[Trait("Category", "Integration")]
public sealed partial class WebDavStorageContractTests(StorageBackendsFixture fixture)
    : IStorageContractTests
{
    [System.Text.RegularExpressions.GeneratedRegex(
        "<D:href>([^<]+)</D:href>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial System.Text.RegularExpressions.Regex DavHref();

    // Raw HTTP client built per-test from the shared fixture's credentials.
    private HttpClient BuildRawHttp()
    {
        HttpClientHandler handler = new()
        {
            Credentials = new NetworkCredential(
                StorageBackendsFixture.WebDavUser,
                StorageBackendsFixture.WebDavPassword
            ),
            PreAuthenticate = true,
        };
        return new(handler, disposeHandler: true);
    }

    // -----------------------------------------------------------------------
    // IStorageContractTests hooks
    // -----------------------------------------------------------------------

    protected override IStorage CreateStorage()
    {
        Skip.If(!fixture.Available, fixture.StartupError ?? "storage container not available");

        return new RemoteStorage(fixture.BuildWebDavDriver());
    }

    /// <summary>
    /// Seed bypasses the driver — PUT directly via raw HTTP.
    /// </summary>
    protected override async Task SeedFile(string relativePath, byte[] content)
    {
        // Ensure parent directories exist.
        string normalized = relativePath.TrimStart('/');
        string? parent = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(parent))
            await SeedDirectory(parent);

        using HttpClient rawHttp = BuildRawHttp();
        string url = fixture.WebDavBaseUrl + normalized;
        using ByteArrayContent body = new(content);
        HttpResponseMessage response = await rawHttp.PutAsync(url, body);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// WebDAV has real directory objects — MKCOL each ancestor in order.
    /// 405 MethodNotAllowed means the collection already exists; that's fine.
    /// </summary>
    protected override async Task SeedDirectory(string relativePath)
    {
        using HttpClient rawHttp = BuildRawHttp();
        string normalized = relativePath.TrimStart('/').TrimEnd('/');
        string[] segments = normalized.Split('/');
        string accumulated = string.Empty;

        foreach (string segment in segments)
        {
            accumulated = string.IsNullOrEmpty(accumulated) ? segment : accumulated + "/" + segment;

            string url = fixture.WebDavBaseUrl + accumulated + "/";
            using HttpRequestMessage mkcol = new(new("MKCOL"), url);
            HttpResponseMessage response = await rawHttp.SendAsync(mkcol);

            if (
                !response.IsSuccessStatusCode
                && response.StatusCode != HttpStatusCode.MethodNotAllowed
            )
                response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>
    /// Verify presence via PROPFIND Depth: 0, bypassing the driver.
    /// Returns true only for HTTP 207 Multi-Status responses.
    /// </summary>
    protected override async Task<bool> BackendHasFile(string relativePath)
    {
        using HttpClient rawHttp = BuildRawHttp();
        string url = fixture.WebDavBaseUrl + relativePath.TrimStart('/');
        using HttpRequestMessage propfind = new(new("PROPFIND"), url);
        propfind.Headers.Add("Depth", "0");
        HttpResponseMessage response = await rawHttp.SendAsync(propfind);
        return response.StatusCode == HttpStatusCode.MultiStatus;
    }

    protected override async Task DisposeStorage()
    {
        // Per-test isolation — wipe everything in the WebDAV server's root
        // before the next test runs. Without this each test sees leftover files
        // from earlier tests, breaking "list shows exactly N entries" assertions.
        if (!fixture.Available)
            return;

        try
        {
            using HttpClient rawHttp = BuildRawHttp();

            // Enumerate top-level resources and DELETE each. Recursive deletes
            // on collections take the whole subtree.
            using HttpRequestMessage propfind = new(new("PROPFIND"), fixture.WebDavBaseUrl);
            propfind.Headers.Add("Depth", "1");
            HttpResponseMessage list = await rawHttp.SendAsync(propfind);
            if (list.StatusCode != HttpStatusCode.MultiStatus)
                return;

            string body = await list.Content.ReadAsStringAsync();
            // Crude href extraction — good enough for this fixture.
            System.Text.RegularExpressions.MatchCollection matches = DavHref().Matches(body);

            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                string href = m.Groups[1].Value;
                // Skip the root itself.
                Uri full = new(new(fixture.WebDavBaseUrl), href);
                if (full.AbsoluteUri.TrimEnd('/') == fixture.WebDavBaseUrl.TrimEnd('/'))
                    continue;

                using HttpRequestMessage del = new(HttpMethod.Delete, full);
                _ = await rawHttp.SendAsync(del);
            }
        }
        catch
        {
            // Best effort — cleanup failures shouldn't fail the test.
        }
    }

    // -----------------------------------------------------------------------
    // WebDAV-specific overrides — document known driver divergences
    // -----------------------------------------------------------------------

    // ExistsAsync for root ("") — bytemark/webdav responds to PROPFIND on "/"
    // with 207. Expected to pass.

    // Backslash normalisation — WebDavStorageDriver replaces '\' with '/' before
    // building the URL. Expected to pass.

    // Double-slash normalisation — WebDavStorageDriver does NOT collapse "//".
    // "foo//bar.bin" and "foo/bar.bin" are different URLs on WebDAV.
    // This test is expected to FAIL until the driver normalises paths.
    // Named distinctly to avoid xUnit1024 (test method name conflict with base class).
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task WebDav_double_slash_is_known_failure_requires_driver_normalisation()
    {
        Skip.If(!fixture.Available, fixture.StartupError ?? "storage container not available");

        IStorage storage = CreateStorage();
        try
        {
            byte[] data = [0x01, 0x02];
            await SeedFile("foo/bar.bin", data);

            bool withSingle = await storage.ExistsAsync("foo/bar.bin", CancellationToken.None);

            // KNOWN FAILURE: WebDavStorageDriver does not collapse double slashes.
            // "foo//bar.bin" is a different WebDAV URL from "foo/bar.bin".
            // Driver fix needed: normalise consecutive slashes before URL construction.
            bool withDouble = await storage.ExistsAsync("foo//bar.bin", CancellationToken.None);

            withSingle.Should().BeTrue();
            withDouble
                .Should()
                .Be(
                    withSingle,
                    "KNOWN FAILURE: WebDavStorageDriver does not collapse double slashes — driver fix needed"
                );
        }
        finally
        {
            await DisposeStorage();
        }
    }

    // Absolute path rejection — RemoteStorage.V() calls StructuralValidate
    // which throws StoragePathNotAllowedException for leading-slash, drive-letter,
    // and UNC paths before the WebDAV driver builds a URL.

    // Null-byte in path — StructuralValidate throws StoragePathNotAllowedException
    // before the driver is reached, satisfying the base contract's "throws" assertion.

    // ".." traversal — StructuralValidate throws StoragePathNotAllowedException
    // before the driver builds any URL.
}
