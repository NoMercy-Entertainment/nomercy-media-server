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

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.App.EmbeddedStaticAssets;

/// <summary>
/// Middleware that serves static files from embedded resources with optimizations
/// similar to MapStaticAssets (caching headers, compression negotiation, ETags).
/// Supports HTML injection for scripts, styles, and meta tags.
/// </summary>
public sealed partial class EmbeddedStaticAssetsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ManifestEmbeddedFileProvider _fileProvider;
    private readonly EmbeddedStaticAssetsOptions _options;
    private readonly FileExtensionContentTypeProvider _contentTypeProvider;
    private readonly ConcurrentDictionary<string, CachedAsset> _assetCache;
    private readonly ILogger<EmbeddedStaticAssetsMiddleware> _logger;
    private readonly string _scriptsToInject;
    private readonly string _stylesToInject;
    private readonly string _metaTagsToInject;

    public EmbeddedStaticAssetsMiddleware(
        RequestDelegate next,
        ManifestEmbeddedFileProvider fileProvider,
        EmbeddedStaticAssetsOptions options,
        ILogger<EmbeddedStaticAssetsMiddleware> logger
    )
    {
        _next = next;
        _fileProvider = fileProvider;
        _options = options;
        _logger = logger;
        _contentTypeProvider = new();
        _assetCache = new(StringComparer.OrdinalIgnoreCase);

        // Pre-build injection strings
        _scriptsToInject = BuildScriptInjection(options.InjectScripts, options.MinifyInjections);
        _stylesToInject = BuildStyleInjection(options.InjectStyles, options.MinifyInjections);
        _metaTagsToInject = BuildMetaTagInjection(options.InjectMetaTags, options.MinifyInjections);

        // Add additional MIME types
        _contentTypeProvider.Mappings[".webmanifest"] = "application/manifest+json";
        _contentTypeProvider.Mappings[".woff2"] = "font/woff2";
        _contentTypeProvider.Mappings[".woff"] = "font/woff";
        _contentTypeProvider.Mappings[".ttf"] = "font/ttf";
        _contentTypeProvider.Mappings[".otf"] = "font/otf";
        _contentTypeProvider.Mappings[".eot"] = "application/vnd.ms-fontobject";
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string path = context.Request.Path.Value.OrEmpty();

        // Skip if not a GET or HEAD request
        if (
            !HttpMethods.IsGet(context.Request.Method)
            && !HttpMethods.IsHead(context.Request.Method)
        )
        {
            await _next(context);
            return;
        }

        // Normalize path - remove leading slash for file provider
        string filePath = path.TrimStart('/');
        if (string.IsNullOrEmpty(filePath))
        {
            filePath = "index.html";
        }

        // Try to get or create cached asset
        CachedAsset? asset = await GetOrCreateCachedAssetAsync(filePath);

        // SPA fallback: if no file found and request looks like a page navigation
        // (no file extension), serve index.html — the Vue router handles the route
        if (asset == null && !Path.HasExtension(filePath))
        {
            asset = await GetOrCreateCachedAssetAsync("index.html");
        }

        if (asset == null)
        {
            await _next(context);
            return;
        }

        // Check for conditional request (If-None-Match)
        string requestETag = context.Request.Headers.IfNoneMatch.ToString();
        if (!string.IsNullOrEmpty(requestETag) && requestETag == asset.ETag)
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        // Determine best encoding based on Accept-Encoding header
        (byte[] content, string? encoding) = SelectBestEncoding(context, asset);

        // Set response headers
        context.Response.ContentType = asset.ContentType;
        context.Response.ContentLength = content.Length;
        context.Response.Headers.ETag = asset.ETag;
        context.Response.Headers.LastModified = asset.LastModified.ToString("R");

        // Set cache control based on whether file has a hash in its name
        if (HasContentHash(filePath))
        {
            // Immutable cache for fingerprinted assets
            context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        }
        else
        {
            // Short cache with revalidation for non-fingerprinted assets
            context.Response.Headers.CacheControl = "public, max-age=3600, must-revalidate";
        }

        if (!string.IsNullOrEmpty(encoding))
        {
            context.Response.Headers.ContentEncoding = encoding;
            context.Response.Headers.Vary = "Accept-Encoding";
        }

        // Write content for GET requests (not HEAD)
        if (HttpMethods.IsGet(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(content);
        }
    }

    private async Task<CachedAsset?> GetOrCreateCachedAssetAsync(string filePath)
    {
        if (_assetCache.TryGetValue(filePath, out CachedAsset? cached))
        {
            return cached;
        }

        IFileInfo fileInfo = _fileProvider.GetFileInfo(filePath);
        if (!fileInfo.Exists)
        {
            return null;
        }

        byte[] content;
        await using (Stream stream = fileInfo.CreateReadStream())
        using (MemoryStream ms = new())
        {
            await stream.CopyToAsync(ms);
            content = ms.ToArray();
        }

        // Determine content type
        if (!_contentTypeProvider.TryGetContentType(filePath, out string? contentType))
        {
            contentType = "application/octet-stream";
        }

        // Apply HTML injection if this is an HTML file matching our patterns
        if (contentType == "text/html" && ShouldInjectHtml(filePath))
        {
            content = InjectHtmlContent(content);
            _logger.LogDebug("Injected scripts/styles into: {Path}", filePath);
        }

        // Generate ETag from content hash (after injection)
        byte[] hashBytes = SHA256.HashData(content);
        string etag = $"\"{Convert.ToBase64String(hashBytes)}\"";

        // Pre-compress content
        byte[] gzipContent = CompressGzip(content);
        byte[] brotliContent = CompressBrotli(content);

        CachedAsset asset = new()
        {
            OriginalContent = content,
            GzipContent = gzipContent,
            BrotliContent = brotliContent,
            ContentType = contentType,
            ETag = etag,
            LastModified = fileInfo.LastModified.UtcDateTime,
        };

        _assetCache.TryAdd(filePath, asset);
        _logger.LogDebug("Cached embedded asset: {Path} ({Size} bytes)", [filePath, content.Length]);

        return asset;
    }

    private bool ShouldInjectHtml(string filePath)
    {
        // Check if we have anything to inject
        if (
            string.IsNullOrEmpty(_scriptsToInject)
            && string.IsNullOrEmpty(_stylesToInject)
            && string.IsNullOrEmpty(_metaTagsToInject)
        )
        {
            return false;
        }

        // Check if file matches any of our patterns
        string fileName = Path.GetFileName(filePath);
        foreach (string pattern in _options.HtmlFilePatterns)
        {
            if (MatchesPattern(filePath, pattern) || MatchesPattern(fileName, pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesPattern(string path, string pattern)
    {
        // Simple glob matching: * matches any characters, ** matches any path
        string regexPattern =
            "^" + Regex.Escape(pattern).Replace(@"\*\*", ".*").Replace("\\*", "[^/]*") + "$";

        return Regex.IsMatch(
            path,
            regexPattern,
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1)
        );
    }

    private byte[] InjectHtmlContent(byte[] content)
    {
        string html = Encoding.UTF8.GetString(content);

        // Inject meta tags and styles before </head>
        if (!string.IsNullOrEmpty(_metaTagsToInject) || !string.IsNullOrEmpty(_stylesToInject))
        {
            string headInjection = _metaTagsToInject + _stylesToInject;
            html = HeadCloseRegex().Replace(html, headInjection + "</head>", 1);
        }

        // Inject scripts before </body>
        if (!string.IsNullOrEmpty(_scriptsToInject))
        {
            html = BodyCloseRegex().Replace(html, _scriptsToInject + "</body>", 1);
        }

        return Encoding.UTF8.GetBytes(html);
    }

    private static string BuildScriptInjection(List<string> scripts, bool minify)
    {
        if (scripts.Count == 0)
            return string.Empty;

        StringBuilder sb = new();
        foreach (string script in scripts)
        {
            if (script.TrimStart().StartsWith("<script", StringComparison.OrdinalIgnoreCase))
            {
                // Already a complete script tag
                sb.Append(minify ? script.Trim() : script);
            }
            else
            {
                // Just a path, wrap in script tag
                sb.Append($"<script src=\"{script}\"></script>");
            }
        }
        return sb.ToString();
    }

    private static string BuildStyleInjection(List<string> styles, bool minify)
    {
        if (styles.Count == 0)
            return string.Empty;

        StringBuilder sb = new();
        foreach (string style in styles)
        {
            if (
                style.TrimStart().StartsWith("<link", StringComparison.OrdinalIgnoreCase)
                || style.TrimStart().StartsWith("<style", StringComparison.OrdinalIgnoreCase)
            )
            {
                // Already a complete tag
                sb.Append(minify ? style.Trim() : style);
            }
            else
            {
                // Just a path, wrap in link tag
                sb.Append($"<link rel=\"stylesheet\" href=\"{style}\">");
            }
        }
        return sb.ToString();
    }

    private static string BuildMetaTagInjection(List<string> metaTags, bool minify)
    {
        if (metaTags.Count == 0)
            return string.Empty;

        StringBuilder sb = new();
        foreach (string meta in metaTags)
        {
            sb.Append(minify ? meta.Trim() : meta);
        }
        return sb.ToString();
    }

    [GeneratedRegex("</head>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HeadCloseRegex();

    [GeneratedRegex("</body>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BodyCloseRegex();

    private static (byte[] content, string? encoding) SelectBestEncoding(
        HttpContext context,
        CachedAsset asset
    )
    {
        string acceptEncoding = context.Request.Headers.AcceptEncoding.ToString();

        // Don't compress already compressed content types
        if (IsAlreadyCompressed(asset.ContentType))
        {
            return (asset.OriginalContent, null);
        }

        // Only compress if content is large enough to benefit
        if (asset.OriginalContent.Length < 1024)
        {
            return (asset.OriginalContent, null);
        }

        // Prefer Brotli over Gzip
        if (
            acceptEncoding.Contains("br", StringComparison.OrdinalIgnoreCase)
            && asset.BrotliContent.Length < asset.OriginalContent.Length
        )
        {
            return (asset.BrotliContent, "br");
        }

        if (
            acceptEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase)
            && asset.GzipContent.Length < asset.OriginalContent.Length
        )
        {
            return (asset.GzipContent, "gzip");
        }

        return (asset.OriginalContent, null);
    }

    private static bool IsAlreadyCompressed(string contentType)
    {
        return contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("zip", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("compressed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasContentHash(string path)
    {
        // Check for common fingerprint patterns like file-abc123.js or file.abc123.js
        string fileName = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);

        // Pattern: name-hash.ext (e.g., app-DxhB2PJG.js)
        int lastDash = fileName.LastIndexOf('-');
        if (lastDash > 0 && lastDash < fileName.Length - 1)
        {
            string potentialHash = fileName[(lastDash + 1)..];
            if (
                potentialHash.Length >= 6
                && potentialHash.All(c => char.IsLetterOrDigit(c) || c == '_')
            )
            {
                return true;
            }
        }

        // Pattern: name.hash.ext (e.g., workbox-f456e5ee.js)
        int lastDot = fileName.LastIndexOf('.');
        if (lastDot > 0 && lastDot < fileName.Length - 1)
        {
            string potentialHash = fileName[(lastDot + 1)..];
            if (
                potentialHash.Length >= 6
                && potentialHash.All(c => char.IsLetterOrDigit(c) || c == '_')
            )
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] CompressGzip(byte[] data)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.SmallestSize))
        {
            gzip.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    private static byte[] CompressBrotli(byte[] data)
    {
        using MemoryStream output = new();
        using (BrotliStream brotli = new(output, CompressionLevel.SmallestSize))
        {
            brotli.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    private sealed class CachedAsset
    {
        public required byte[] OriginalContent { get; init; }
        public required byte[] GzipContent { get; init; }
        public required byte[] BrotliContent { get; init; }
        public required string ContentType { get; init; }
        public required string ETag { get; init; }
        public required DateTime LastModified { get; init; }
    }
}
