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
using NoMercy.NmSystem.Information;
using NoMercy.Tests.Api.Infrastructure;
using ImageMagick;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// Who may reach <c>/images/{type}/{path}</c>. GET is public on purpose: the
/// clients load artwork through img tags and CSS backgrounds, which carry no
/// header. DELETE drops a cached rendition and needs the MediaAccess policy.
/// (#478)
/// </summary>
[Trait("Category", "Characterization")]
public class ImageControllerAnonymousAccessTests(NoMercyApiFactory factory)
    : IClassFixture<NoMercyApiFactory>
{
    private const string TypeFolder = "accesstype";

    private static string CreateImage(out string name)
    {
        string folder = Path.Join(AppFiles.ImagesPath, TypeFolder);
        Directory.CreateDirectory(folder);
        name = $"access_{Guid.NewGuid():N}.png";
        string file = Path.Join(folder, name);
        using MagickImage image = new(new MagickColor(0, 255, 0), 20, 10);
        image.Write(file, MagickFormat.Png);
        return file;
    }

    [Fact]
    public async Task AnonymousGet_ReturnsTheImage()
    {
        string file = CreateImage(out string name);
        try
        {
            HttpClient client = factory.CreateClient().AsUnauthenticated();

            HttpResponseMessage response = await client.GetAsync($"/images/{TypeFolder}/{name}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task AnonymousDelete_IsRejectedWith401()
    {
        HttpClient client = factory.CreateClient().AsUnauthenticated();

        HttpResponseMessage response = await client.DeleteAsync($"/images/{TypeFolder}/none.png");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ByUserWithoutMediaAccess_IsRejectedWith403()
    {
        HttpClient client = factory.CreateClient().AsAuthenticated();
        client.DefaultRequestHeaders.Add(
            TestAuthDefaults.TestUserIdHeader,
            Guid.NewGuid().ToString()
        );

        HttpResponseMessage response = await client.DeleteAsync($"/images/{TypeFolder}/none.png");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ByUserWithMediaAccess_ReachesTheAction()
    {
        HttpClient client = factory.CreateClient().AsAuthenticated();

        HttpResponseMessage response = await client.DeleteAsync($"/images/{TypeFolder}/none.png");

        // The action answers 404 "Cache not found" when no rendition is cached:
        // proof the request got past authorization and into DeleteCache.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Cache not found", body);
    }
}
