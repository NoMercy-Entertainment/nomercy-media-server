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

[Trait("Category", "Characterization")]
public class ImageControllerTests : IClassFixture<NoMercyApiFactory>, IDisposable
{
    private readonly HttpClient _client;
    private readonly string _testTypeFolder;
    private readonly string _testId = Guid.NewGuid().ToString("N")[..8];
    private readonly string _testImageName;
    private readonly string _testSvgName;

    public ImageControllerTests(NoMercyApiFactory factory)
    {
        _client = factory.CreateClient();
        _client.AsAuthenticated();

        _testImageName = $"testimage_{_testId}.png";
        _testSvgName = $"testimage_{_testId}.svg";

        _testTypeFolder = Path.Join(AppFiles.ImagesPath, "testtype");
        if (!Directory.Exists(_testTypeFolder))
            Directory.CreateDirectory(_testTypeFolder);

        // Ensure temp images directory exists
        if (!Directory.Exists(AppFiles.TempImagesPath))
            Directory.CreateDirectory(AppFiles.TempImagesPath);

        // Create a real 200x100 PNG test image
        using (MagickImage image = new(new MagickColor(255, 0, 0), 200, 100))
        {
            image.Write(Path.Join(_testTypeFolder, _testImageName), MagickFormat.Png);
        }

        // Create a minimal SVG test file
        File.WriteAllText(
            Path.Join(_testTypeFolder, _testSvgName),
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\"><rect fill=\"red\" width=\"100\" height=\"100\"/></svg>"
        );
    }

    public void Dispose()
    {
        // Clean up test-specific files
        string imagePath = Path.Join(_testTypeFolder, _testImageName);
        string svgPath = Path.Join(_testTypeFolder, _testSvgName);
        try
        {
            if (File.Exists(imagePath))
                File.Delete(imagePath);
        }
        catch { }
        try
        {
            if (File.Exists(svgPath))
                File.Delete(svgPath);
        }
        catch { }

        // Clean up cached images created during tests
        foreach (string file in Directory.GetFiles(AppFiles.TempImagesPath))
        {
            try
            {
                File.Delete(file);
            }
            catch
            { /* best effort */
            }
        }
    }

    [Fact]
    public async Task Image_NoParams_ReturnsOriginalFile()
    {
        // No width, type, or quality params → emptyArguments = true → returns original
        HttpResponseMessage response = await _client.GetAsync($"/images/testtype/{_testImageName}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string contentType = response.Content.Headers.ContentType!.MediaType!;
        Assert.Equal("image/png", contentType);

        byte[] originalBytes = await File.ReadAllBytesAsync(
            Path.Join(_testTypeFolder, _testImageName)
        );
        byte[] responseBytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(originalBytes.Length, responseBytes.Length);
    }

    [Fact]
    public async Task Image_WithWidth_ReturnsResizedImage()
    {
        // Width=50 → emptyArguments = false → image processing pipeline runs
        HttpResponseMessage response = await _client.GetAsync(
            $"/images/testtype/{_testImageName}?width=50"
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        byte[] responseBytes = await response.Content.ReadAsByteArrayAsync();
        using MagickImage resultImage = new(responseBytes);

        // Resized to width=50, aspect ratio preserved (200x100 → 50x25)
        Assert.Equal(50u, resultImage.Width);
        Assert.Equal(25u, resultImage.Height);
    }

    [Fact]
    public async Task Image_WithQualityNotDefault_ReturnsProcessedImage()
    {
        // Quality=80 → emptyArguments = false → processing runs
        HttpResponseMessage response = await _client.GetAsync(
            $"/images/testtype/{_testImageName}?quality=80"
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        byte[] responseBytes = await response.Content.ReadAsByteArrayAsync();
        // The image was processed (not the raw original) — response should be valid image data
        Assert.True(responseBytes.Length > 0);
    }

    [Fact]
    public async Task Image_WithType_ReturnsProcessedImage()
    {
        // Type=png → emptyArguments = false (Type is not null) → processing runs
        HttpResponseMessage response = await _client.GetAsync(
            $"/images/testtype/{_testImageName}?type=png&width=100"
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        byte[] responseBytes = await response.Content.ReadAsByteArrayAsync();
        using MagickImage resultImage = new(responseBytes);
        Assert.Equal(100u, resultImage.Width);
    }

    [Fact]
    public async Task Image_SvgBypassesProcessing()
    {
        // SVG files should bypass processing regardless of params
        HttpResponseMessage response = await _client.GetAsync(
            $"/images/testtype/{_testSvgName}?width=50"
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        byte[] originalBytes = await File.ReadAllBytesAsync(
            Path.Join(_testTypeFolder, _testSvgName)
        );
        byte[] responseBytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(originalBytes.Length, responseBytes.Length);
    }

    [Fact]
    public async Task Image_ProcessedImageIsCached()
    {
        // First request: processes and caches
        HttpResponseMessage response1 = await _client.GetAsync(
            $"/images/testtype/{_testImageName}?width=75"
        );
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        byte[] firstBytes = await response1.Content.ReadAsByteArrayAsync();

        // Second request: should serve from cache
        HttpResponseMessage response2 = await _client.GetAsync(
            $"/images/testtype/{_testImageName}?width=75"
        );
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        byte[] secondBytes = await response2.Content.ReadAsByteArrayAsync();

        Assert.Equal(firstBytes.Length, secondBytes.Length);
    }

    [Fact]
    public async Task Image_NonExistentType_Returns404()
    {
        HttpResponseMessage response = await _client.GetAsync("/images/nonexistent/test.png");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Image_NonExistentFile_Returns404()
    {
        HttpResponseMessage response = await _client.GetAsync(
            $"/images/testtype/doesnotexist_{_testId}.png"
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Image_DefaultQuality100_NoWidth_NoType_ReturnsOriginal()
    {
        // Explicitly set quality=100 (the default) with no width/type → emptyArguments = true
        HttpResponseMessage response = await _client.GetAsync(
            $"/images/testtype/{_testImageName}?quality=100"
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        byte[] originalBytes = await File.ReadAllBytesAsync(
            Path.Join(_testTypeFolder, _testImageName)
        );
        byte[] responseBytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(originalBytes.Length, responseBytes.Length);
    }

    [Fact]
    public async Task Image_CachingHeaders_AreSet()
    {
        HttpResponseMessage response = await _client.GetAsync($"/images/testtype/{_testImageName}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Cache-Control"));
        Assert.Contains("public", response.Headers.GetValues("Cache-Control").First());
    }

    [Fact]
    public async Task Image_WithWidthAndAspectRatio_ReturnsCustomDimensions()
    {
        // Width=100 with aspect_ratio=2.0 → 100x200
        HttpResponseMessage response = await _client.GetAsync(
            $"/images/testtype/{_testImageName}?width=100&aspect_ratio=2.0"
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        byte[] responseBytes = await response.Content.ReadAsByteArrayAsync();
        using MagickImage resultImage = new(responseBytes);
        Assert.Equal(100u, resultImage.Width);
        Assert.Equal(200u, resultImage.Height);
    }
}
