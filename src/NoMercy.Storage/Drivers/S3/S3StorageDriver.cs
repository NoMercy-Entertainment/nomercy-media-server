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
using System.Text;
using System.Xml.Linq;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using NoMercy.Storage.Common;

namespace NoMercy.Storage.Drivers.S3;

/// <summary>
/// <see cref="IStorageDriver"/> backed by any S3-compatible object store
/// (AWS S3, Cloudflare R2, MinIO, DigitalOcean Spaces, …).
///
/// Read operations (GET, HEAD, DELETE, LIST) use raw SigV4 HTTP — the AWS SDK's
/// signing path is unreliable against MinIO and other non-AWS endpoints.
/// Copy/Move still go through the SDK because there is no raw SigV4 server-side
/// copy in the non-AWS case and those operations are infrequent.
/// </summary>
public sealed class S3StorageDriver : IStorageDriver, IDisposable
{
    public string BackendLabel => "S3";

    // SDK client kept only for CopyObject / server-side operations.
    private readonly IAmazonS3? _client;

    private readonly string _bucket;
    private readonly string _prefix;

    // Multipart part size handed to the write stream. Larger parts mean fewer HTTP
    // round-trips at the cost of a bigger in-flight buffer. Defaults to the write
    // stream's own default; the throughput sweep sets it to measure the curve.
    // Not exposed on IStorageDriver — an internal tuning seam only.
    internal int StreamPartSize { get; set; } = 8 * 1024 * 1024;

    private readonly string? _endpoint;
    private readonly string? _region;
    private readonly string? _accessKey;
    private readonly string? _secretKey;

    // Shared HttpClient — thread-safe, reuse across all driver instances.
    private static readonly HttpClient Http = new();

    /// <param name="bucket">S3 bucket name.</param>
    /// <param name="region">AWS region string (e.g. <c>us-east-1</c>).</param>
    /// <param name="prefix">
    ///   Optional key prefix prepended to every path. A trailing slash is
    ///   added automatically so callers never need to include it.
    /// </param>
    /// <param name="endpoint">
    ///   Optional service URL override. Required for non-AWS providers
    ///   (R2, MinIO, Spaces). <c>ForcePathStyle</c> is enabled automatically
    ///   when this is set.
    /// </param>
    /// <param name="accessKey">AWS access key ID. When null, the default
    ///   credential chain is used (env-vars / IAM role).</param>
    /// <param name="secretKey">AWS secret access key. Required when
    ///   <paramref name="accessKey"/> is supplied.</param>
    public S3StorageDriver(
        string bucket,
        string region,
        string? prefix = null,
        string? endpoint = null,
        string? accessKey = null,
        string? secretKey = null
    )
    {
        if (string.IsNullOrWhiteSpace(bucket))
            throw new ArgumentException("bucket must not be empty", nameof(bucket));
        if (string.IsNullOrWhiteSpace(region))
            throw new ArgumentException("region must not be empty", nameof(region));

        _bucket = bucket;
        _prefix = string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix.TrimEnd('/') + "/";
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint;
        _region = region;
        _accessKey = accessKey;
        _secretKey = secretKey;

        // Build SDK client only when we have credentials or an endpoint — used
        // only for CopyObject / DeleteObjects batch operations.
        AmazonS3Config config;
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            config = new()
            {
                ServiceURL = endpoint,
                ForcePathStyle = true,
                AuthenticationRegion = region,
            };
        }
        else
        {
            config = new() { RegionEndpoint = RegionEndpoint.GetBySystemName(region) };
        }

        _client =
            accessKey is not null && secretKey is not null
                ? new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), config)
                : new AmazonS3Client(config);
    }

    /// <summary>
    /// Exposed for testing — allows injection of a pre-configured SDK client
    /// (e.g. one pointing at a Testcontainers MinIO instance).
    /// Raw-SigV4 paths are not available when constructed this way because
    /// endpoint / key fields are absent.
    /// </summary>
    internal S3StorageDriver(IAmazonS3 client, string bucket, string? prefix = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _bucket = bucket ?? throw new ArgumentNullException(nameof(bucket));
        _prefix = string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix.TrimEnd('/') + "/";
    }

    // -----------------------------------------------------------------------
    // Key helpers
    // -----------------------------------------------------------------------

    private string ToKey(string path)
    {
        string normalized = path.TrimStart('/').TrimStart('\\').Replace('\\', '/');
        // Path Contract Rule 2: collapse consecutive separators. MinIO
        // returns HTTP 400 InvalidArgument on keys with "//"; canonicalize
        // before sending. Public S3 silently maps "//" to "/" but we'd lose
        // the round-trip property if we relied on that.
        while (normalized.Contains("//"))
            normalized = normalized.Replace("//", "/");
        return _prefix + normalized;
    }

    private string FromKey(string key) =>
        string.IsNullOrEmpty(_prefix) ? key : key.Substring(_prefix.Length);

    // -----------------------------------------------------------------------
    // Raw-SigV4 HTTP helpers
    // -----------------------------------------------------------------------

    private bool HasRawCredentials =>
        _endpoint is not null && _accessKey is not null && _secretKey is not null;

    /// <summary>
    /// Builds the base URL for a path-style request: <c>endpoint/bucket/key</c>.
    /// </summary>
    private Uri ObjectUrl(string key) =>
        new(
            _endpoint!.TrimEnd('/')
                + "/"
                + Uri.EscapeDataString(_bucket)
                + "/"
                + S3SigV4.EscapeKey(key)
        );

    private HttpRequestMessage SignedRequest(HttpMethod method, string key, string canonicalQs = "")
    {
        (string authHeader, string amzDate) = S3SigV4.SignHeaderRequest(
            method.Method,
            _endpoint!,
            _bucket,
            key,
            canonicalQs,
            _region!,
            _accessKey!,
            _secretKey!,
            DateTime.UtcNow
        );

        string host = S3SigV4.HostFromEndpoint(_endpoint!);
        const string payloadHash =
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        Uri uri = string.IsNullOrEmpty(canonicalQs)
            ? ObjectUrl(key)
            : new(ObjectUrl(key).ToString() + "?" + canonicalQs);

        HttpRequestMessage req = new(method, uri);
        req.Headers.TryAddWithoutValidation("Authorization", authHeader);
        req.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        req.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        req.Headers.Host = host;
        return req;
    }

    // -----------------------------------------------------------------------
    // IStorageDriver — existence / metadata
    // -----------------------------------------------------------------------

    public bool FileExists(string path)
    {
        string key = ToKey(path);

        if (HasRawCredentials)
        {
            using HttpRequestMessage req = SignedRequest(HttpMethod.Head, key);
            using HttpResponseMessage res = Http.Send(req);
            if (res.StatusCode == HttpStatusCode.NotFound)
                return false;
            res.EnsureSuccessStatusCode();
            return true;
        }

        // SDK fallback (test injection path)
        try
        {
            GetObjectMetadataRequest request = new() { BucketName = _bucket, Key = key };
            _client!.GetObjectMetadataAsync(request).GetAwaiter().GetResult();
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public bool DirectoryExists(string path)
    {
        string prefix = ToKey(path).TrimEnd('/') + "/";

        if (HasRawCredentials)
        {
            // A child under the prefix proves the directory exists. ParseListXml
            // filters the trailing-slash marker key out of its results, so an
            // empty directory created via CreateDirectory (which writes just a
            // "prefix/" marker object) needs a direct HEAD on the marker too.
            IEnumerable<(string key, bool isDir)> page = ListOnePage(
                prefix,
                delimiter: "/",
                maxKeys: 1
            );
            if (page.Any())
                return true;

            using HttpRequestMessage marker = SignedRequest(HttpMethod.Head, prefix);
            using HttpResponseMessage markerRes = Http.Send(marker);
            return markerRes.IsSuccessStatusCode;
        }

        ListObjectsV2Request request = new()
        {
            BucketName = _bucket,
            Prefix = prefix,
            MaxKeys = 1,
        };
        ListObjectsV2Response response = _client!
            .ListObjectsV2Async(request)
            .GetAwaiter()
            .GetResult();
        return response.S3Objects.Count > 0 || response.CommonPrefixes.Count > 0;
    }

    public long GetFileSize(string path)
    {
        string key = ToKey(path);

        if (HasRawCredentials)
        {
            using HttpRequestMessage req = SignedRequest(HttpMethod.Head, key);
            using HttpResponseMessage res = Http.Send(req);
            if (res.StatusCode == HttpStatusCode.NotFound)
                return 0L;
            res.EnsureSuccessStatusCode();
            return res.Content.Headers.ContentLength ?? 0L;
        }

        GetObjectMetadataRequest request = new() { BucketName = _bucket, Key = key };
        GetObjectMetadataResponse response = _client!
            .GetObjectMetadataAsync(request)
            .GetAwaiter()
            .GetResult();
        return response.ContentLength;
    }

    public DateTime GetLastWriteTimeUtc(string path)
    {
        string key = ToKey(path);

        if (HasRawCredentials)
        {
            using HttpRequestMessage req = SignedRequest(HttpMethod.Head, key);
            using HttpResponseMessage res = Http.Send(req);
            if (res.StatusCode == HttpStatusCode.NotFound)
                return DateTime.UtcNow;
            res.EnsureSuccessStatusCode();
            DateTimeOffset? lastMod = res.Content.Headers.LastModified;
            return lastMod.HasValue ? lastMod.Value.UtcDateTime : DateTime.UtcNow;
        }

        GetObjectMetadataRequest request = new() { BucketName = _bucket, Key = key };
        GetObjectMetadataResponse response = _client!
            .GetObjectMetadataAsync(request)
            .GetAwaiter()
            .GetResult();
        return response.LastModified?.ToUniversalTime() ?? DateTime.UtcNow;
    }

    // S3 does not expose ctime or atime.
    public DateTime GetCreationTimeUtc(string path) => GetLastWriteTimeUtc(path);

    public DateTime GetLastAccessTimeUtc(string path) => GetLastWriteTimeUtc(path);

    // -----------------------------------------------------------------------
    // IStorageDriver — read
    // -----------------------------------------------------------------------

    public Stream OpenRead(string path)
    {
        string key = ToKey(path);

        if (HasRawCredentials)
        {
            HttpRequestMessage req = SignedRequest(HttpMethod.Get, key);
            // ResponseHeadersRead: body is not buffered — stream owned by caller.
            HttpResponseMessage res = Http.Send(req, HttpCompletionOption.ResponseHeadersRead);
            res.EnsureSuccessStatusCode();
            // Wrap so the HttpResponseMessage is disposed when the stream is closed.
            return new HttpResponseStream(res);
        }

        GetObjectRequest request = new() { BucketName = _bucket, Key = key };
        GetObjectResponse response = _client!.GetObjectAsync(request).GetAwaiter().GetResult();
        return response.ResponseStream;
    }

    // -----------------------------------------------------------------------
    // IStorageDriver — write
    // -----------------------------------------------------------------------

    public Stream OpenWrite(string path, bool overwrite)
    {
        if (!overwrite && FileExists(path))
            throw new IOException(
                $"Cannot write to '{path}': the key already exists and overwrite is false."
            );

        string key = ToKey(path);
        if (_endpoint is null || _accessKey is null || _secretKey is null)
            throw new InvalidOperationException(
                "S3WriteStream requires an explicit endpoint + accessKey + secretKey. "
                    + "OpenWrite is currently not supported on the default-credential-chain path."
            );
        return new S3WriteStream(
            _client!,
            _bucket,
            key,
            _endpoint,
            _region!,
            _accessKey,
            _secretKey,
            partSize: StreamPartSize
        );
    }

    // -----------------------------------------------------------------------
    // IStorageDriver — delete
    // -----------------------------------------------------------------------

    public void DeleteFile(string path)
    {
        string key = ToKey(path);

        if (HasRawCredentials)
        {
            using HttpRequestMessage req = SignedRequest(HttpMethod.Delete, key);
            using HttpResponseMessage res = Http.Send(req);
            if (res.StatusCode != HttpStatusCode.NoContent && res.StatusCode != HttpStatusCode.OK)
                res.EnsureSuccessStatusCode();
            return;
        }

        DeleteObjectRequest request = new() { BucketName = _bucket, Key = key };
        _client!.DeleteObjectAsync(request).GetAwaiter().GetResult();
    }

    public void DeleteDirectory(string path, bool recursive)
    {
        if (!recursive)
        {
            string key = ToKey(path).TrimEnd('/') + "/";
            if (HasRawCredentials)
            {
                using HttpRequestMessage req = SignedRequest(
                    HttpMethod.Delete,
                    key.TrimEnd('/') + "/"
                );
                using HttpResponseMessage res = Http.Send(req);
                return;
            }
            DeleteObjectRequest request = new() { BucketName = _bucket, Key = key };
            _client!.DeleteObjectAsync(request).GetAwaiter().GetResult();
            return;
        }

        string prefix = ToKey(path).TrimEnd('/') + "/";
        string? continuationToken = null;

        do
        {
            IReadOnlyList<string> keys = ListPageKeys(
                prefix,
                delimiter: null,
                continuationToken,
                out continuationToken
            );

            if (keys.Count == 0)
                break;

            // Batch-delete via SDK (no raw equivalent without multi-delete XML body builder).
            DeleteObjectsRequest deleteRequest = new()
            {
                BucketName = _bucket,
                Objects = [.. keys.Select(k => new KeyVersion { Key = k })],
            };
            _client!.DeleteObjectsAsync(deleteRequest).GetAwaiter().GetResult();
        } while (continuationToken is not null);
    }

    // -----------------------------------------------------------------------
    // IStorageDriver — directory creation / helpers
    // -----------------------------------------------------------------------

    public void CreateDirectory(string path)
    {
        string key = ToKey(path).TrimEnd('/') + "/";
        PutObjectRequest request = new()
        {
            BucketName = _bucket,
            Key = key,
            ContentBody = string.Empty,
        };
        _client!.PutObjectAsync(request).GetAwaiter().GetResult();
    }

    // -----------------------------------------------------------------------
    // IStorageDriver — copy / move
    // -----------------------------------------------------------------------

    public void MoveFile(string source, string destination)
    {
        CopyObjectRequest copyRequest = new()
        {
            SourceBucket = _bucket,
            SourceKey = ToKey(source),
            DestinationBucket = _bucket,
            DestinationKey = ToKey(destination),
        };
        _client!.CopyObjectAsync(copyRequest).GetAwaiter().GetResult();
        DeleteFile(source);
    }

    public void CopyFile(string source, string destination, bool overwrite)
    {
        if (!overwrite && FileExists(destination))
            throw new IOException(
                $"Cannot copy to '{destination}': the key already exists and overwrite is false."
            );

        CopyObjectRequest request = new()
        {
            SourceBucket = _bucket,
            SourceKey = ToKey(source),
            DestinationBucket = _bucket,
            DestinationKey = ToKey(destination),
        };
        _client!.CopyObjectAsync(request).GetAwaiter().GetResult();
    }

    public void MoveDirectory(string source, string destination)
    {
        string srcPrefix = ToKey(source).TrimEnd('/') + "/";
        string dstPrefix = ToKey(destination).TrimEnd('/') + "/";
        string? continuationToken = null;

        do
        {
            IReadOnlyList<string> keys = ListPageKeys(
                srcPrefix,
                delimiter: null,
                continuationToken,
                out continuationToken
            );

            foreach (string key in keys)
            {
                string newKey = dstPrefix + key.Substring(srcPrefix.Length);
                CopyObjectRequest copyRequest = new()
                {
                    SourceBucket = _bucket,
                    SourceKey = key,
                    DestinationBucket = _bucket,
                    DestinationKey = newKey,
                };
                _client!.CopyObjectAsync(copyRequest).GetAwaiter().GetResult();
            }

            if (keys.Count > 0)
            {
                DeleteObjectsRequest deleteRequest = new()
                {
                    BucketName = _bucket,
                    Objects = [.. keys.Select(k => new KeyVersion { Key = k })],
                };
                _client!.DeleteObjectsAsync(deleteRequest).GetAwaiter().GetResult();
            }
        } while (continuationToken is not null);
    }

    // -----------------------------------------------------------------------
    // IStorageDriver — enumerate
    // -----------------------------------------------------------------------

    public IEnumerable<string> EnumerateFileSystemEntries(
        string directory,
        string searchPattern,
        SearchOption option
    )
    {
        string prefix = ToKey(directory).TrimEnd('/');
        if (!string.IsNullOrEmpty(prefix))
            prefix += "/";

        bool recursive = option == SearchOption.AllDirectories;

        if (HasRawCredentials)
            return EnumerateRaw(prefix, searchPattern, recursive);

        return EnumerateSdk(prefix, searchPattern, recursive);
    }

    private IEnumerable<string> EnumerateRaw(string prefix, string searchPattern, bool recursive)
    {
        string? continuationToken = null;
        List<string> results = [];

        do
        {
            (List<string> files, List<string> dirs, string? next) = ListPageRaw(
                prefix,
                delimiter: recursive ? null : "/",
                continuationToken
            );

            foreach (string key in files)
            {
                string relPath = FromKey(key);
                string fileName = relPath.Contains('/')
                    ? relPath.Substring(relPath.LastIndexOf('/') + 1)
                    : relPath;
                if (StoragePatternMatcher.Matches(fileName, searchPattern))
                    results.Add(relPath);
            }

            if (!recursive)
            {
                foreach (string commonPrefix in dirs)
                {
                    string relPath = FromKey(commonPrefix.TrimEnd('/'));
                    string dirName = relPath.Contains('/')
                        ? relPath.Substring(relPath.LastIndexOf('/') + 1)
                        : relPath;
                    if (StoragePatternMatcher.Matches(dirName, searchPattern))
                        results.Add(relPath);
                }
            }

            continuationToken = next;
        } while (continuationToken is not null);

        return results;
    }

    private IEnumerable<string> EnumerateSdk(string prefix, string searchPattern, bool recursive)
    {
        string? continuationToken = null;
        List<string> results = [];

        do
        {
            ListObjectsV2Request request = new()
            {
                BucketName = _bucket,
                Prefix = prefix,
                ContinuationToken = continuationToken,
                MaxKeys = 1000,
            };

            if (!recursive)
                request.Delimiter = "/";

            ListObjectsV2Response response = _client!
                .ListObjectsV2Async(request)
                .GetAwaiter()
                .GetResult();

            foreach (S3Object obj in response.S3Objects)
            {
                if (obj.Key.EndsWith("/", StringComparison.Ordinal))
                    continue;

                string relPath = FromKey(obj.Key);
                string fileName = relPath.Contains('/')
                    ? relPath.Substring(relPath.LastIndexOf('/') + 1)
                    : relPath;

                if (StoragePatternMatcher.Matches(fileName, searchPattern))
                    results.Add(relPath);
            }

            if (!recursive)
            {
                foreach (string commonPrefix in response.CommonPrefixes)
                {
                    string relPath = FromKey(commonPrefix.TrimEnd('/'));
                    string dirName = relPath.Contains('/')
                        ? relPath.Substring(relPath.LastIndexOf('/') + 1)
                        : relPath;

                    if (StoragePatternMatcher.Matches(dirName, searchPattern))
                        results.Add(relPath);
                }
            }

            continuationToken =
                response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (continuationToken is not null);

        return results;
    }

    /// <summary>
    /// Batched listing path. ListObjectsV2 / EnumerateRaw already returns
    /// Size + LastModified for every object in the same response — emit them
    /// directly instead of forcing RemoteStorage.List into N×HEAD calls per
    /// file (which turned a 200-segment video_*/ into a 2-3 minute round trip).
    /// </summary>
    public IEnumerable<StorageEntryInfo> EnumerateEntries(
        string directory,
        string searchPattern,
        SearchOption option
    )
    {
        string prefix = ToKey(directory).TrimEnd('/');
        if (!string.IsNullOrEmpty(prefix))
            prefix += "/";

        bool recursive = option == SearchOption.AllDirectories;

        return HasRawCredentials
            ? EnumerateEntriesRaw(prefix, searchPattern, recursive)
            : EnumerateEntriesSdk(prefix, searchPattern, recursive);
    }

    private IEnumerable<StorageEntryInfo> EnumerateEntriesSdk(
        string prefix,
        string searchPattern,
        bool recursive
    )
    {
        string? continuationToken = null;
        List<StorageEntryInfo> results = [];

        do
        {
            ListObjectsV2Request request = new()
            {
                BucketName = _bucket,
                Prefix = prefix,
                ContinuationToken = continuationToken,
                MaxKeys = 1000,
            };

            if (!recursive)
                request.Delimiter = "/";

            ListObjectsV2Response response = _client!
                .ListObjectsV2Async(request)
                .GetAwaiter()
                .GetResult();

            foreach (S3Object obj in response.S3Objects)
            {
                if (obj.Key.EndsWith("/", StringComparison.Ordinal))
                    continue;

                string relPath = FromKey(obj.Key);
                string fileName = relPath.Contains('/')
                    ? relPath.Substring(relPath.LastIndexOf('/') + 1)
                    : relPath;

                if (!StoragePatternMatcher.Matches(fileName, searchPattern))
                    continue;

                results.Add(
                    new(
                        relPath,
                        IsDirectory: false,
                        Size: obj.Size ?? 0L,
                        LastWriteUtc: obj.LastModified is DateTime lm
                            ? lm.ToUniversalTime()
                            : DateTime.UtcNow
                    )
                );
            }

            if (!recursive)
            {
                foreach (string commonPrefix in response.CommonPrefixes)
                {
                    string relPath = FromKey(commonPrefix.TrimEnd('/'));
                    string dirName = relPath.Contains('/')
                        ? relPath.Substring(relPath.LastIndexOf('/') + 1)
                        : relPath;

                    if (StoragePatternMatcher.Matches(dirName, searchPattern))
                        results.Add(
                            new(relPath, IsDirectory: true, Size: 0L, LastWriteUtc: DateTime.UtcNow)
                        );
                }
            }

            continuationToken =
                response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (continuationToken is not null);

        return results;
    }

    private IEnumerable<StorageEntryInfo> EnumerateEntriesRaw(
        string prefix,
        string searchPattern,
        bool recursive
    )
    {
        string? continuationToken = null;
        List<StorageEntryInfo> results = [];

        do
        {
            (
                List<(string Key, long Size, DateTime LastModified)> files,
                List<string> dirs,
                string? next
            ) = ListPageRawWithMeta(prefix, delimiter: recursive ? null : "/", continuationToken);

            foreach ((string key, long size, DateTime lastModified) in files)
            {
                string relPath = FromKey(key);
                string fileName = relPath.Contains('/')
                    ? relPath.Substring(relPath.LastIndexOf('/') + 1)
                    : relPath;
                if (StoragePatternMatcher.Matches(fileName, searchPattern))
                    results.Add(
                        new(relPath, IsDirectory: false, Size: size, LastWriteUtc: lastModified)
                    );
            }

            if (!recursive)
            {
                foreach (string commonPrefix in dirs)
                {
                    string relPath = FromKey(commonPrefix.TrimEnd('/'));
                    string dirName = relPath.Contains('/')
                        ? relPath.Substring(relPath.LastIndexOf('/') + 1)
                        : relPath;
                    if (StoragePatternMatcher.Matches(dirName, searchPattern))
                        results.Add(
                            new(relPath, IsDirectory: true, Size: 0L, LastWriteUtc: DateTime.UtcNow)
                        );
                }
            }

            continuationToken = next;
        } while (continuationToken is not null);

        return results;
    }

    // -----------------------------------------------------------------------
    // IStorageDriver — misc
    // -----------------------------------------------------------------------

    public string GetFullPath(string path)
    {
        string normalized = path.Replace('\\', '/').Trim('/');
        return string.IsNullOrEmpty(_prefix) ? normalized : _prefix.TrimEnd('/') + "/" + normalized;
    }

    public string? ResolveLinkTarget(string path) => null;

    public bool IsHidden(string path) => false;

    // -----------------------------------------------------------------------
    // Presigned URL
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns a time-limited presigned GET URL that the client can use to
    /// fetch the object directly from the S3 backend, bypassing the server.
    /// Returns null when raw credentials are not available (SDK-injected test path).
    /// TTL is clamped to [60s, 24h].
    /// </summary>
    public Task<Uri?> TryGetPresignedUrlAsync(string path, TimeSpan ttl, CancellationToken ct)
    {
        if (!HasRawCredentials)
            return Task.FromResult<Uri?>(null);

        string key = ToKey(path);
        Uri url = S3SigV4.BuildPresignedGetUrl(
            _endpoint!,
            _bucket,
            key,
            _region!,
            _accessKey!,
            _secretKey!,
            ttl,
            DateTime.UtcNow
        );
        return Task.FromResult<Uri?>(url);
    }

    // -----------------------------------------------------------------------
    // Raw LIST helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Fetches a single page of LIST results using raw SigV4.
    /// Returns (file keys, common-prefix strings, next continuation token).
    /// </summary>
    private (List<string> Files, List<string> Dirs, string? Next) ListPageRaw(
        string prefix,
        string? delimiter,
        string? continuationToken,
        bool includeDirectoryMarkers = false
    )
    {
        StringBuilder qs = new();
        qs.Append("list-type=2");
        if (!string.IsNullOrEmpty(prefix))
            qs.Append("&prefix=").Append(Uri.EscapeDataString(prefix));
        if (!string.IsNullOrEmpty(delimiter))
            qs.Append("&delimiter=").Append(Uri.EscapeDataString(delimiter));
        qs.Append("&max-keys=1000");
        if (!string.IsNullOrEmpty(continuationToken))
            qs.Append("&continuation-token=").Append(Uri.EscapeDataString(continuationToken));

        string canonicalQs = BuildSortedQueryString(qs.ToString());

        (string authHeader, string amzDate) = S3SigV4.SignHeaderRequest(
            "GET",
            _endpoint!,
            _bucket,
            string.Empty,
            canonicalQs,
            _region!,
            _accessKey!,
            _secretKey!,
            DateTime.UtcNow
        );

        // List endpoint: endpoint/bucket/?list-type=2&...
        string listUrl =
            _endpoint!.TrimEnd('/') + "/" + Uri.EscapeDataString(_bucket) + "/?" + canonicalQs;

        using HttpRequestMessage req = new(HttpMethod.Get, listUrl);
        req.Headers.TryAddWithoutValidation("Authorization", authHeader);
        req.Headers.TryAddWithoutValidation(
            "x-amz-content-sha256",
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        );
        req.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        req.Headers.Host = S3SigV4.HostFromEndpoint(_endpoint!);

        using HttpResponseMessage res = Http.Send(req);
        if (!res.IsSuccessStatusCode)
        {
            string body = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            throw new IOException(
                $"S3 LIST '{listUrl}' failed: HTTP {(int)res.StatusCode} {res.ReasonPhrase}; body: {body}"
            );
        }

        string xml = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return ParseListXml(xml, includeDirectoryMarkers);
    }

    /// <summary>
    /// Fetches all keys under a prefix (one page, for use in DirectoryExists).
    /// Returns raw S3 keys (not driver-relative).
    /// </summary>
    private IEnumerable<(string Key, bool IsDir)> ListOnePage(
        string prefix,
        string? delimiter,
        int maxKeys
    )
    {
        if (!HasRawCredentials)
        {
            ListObjectsV2Request request = new()
            {
                BucketName = _bucket,
                Prefix = prefix,
                MaxKeys = maxKeys,
                Delimiter = delimiter,
            };
            ListObjectsV2Response response = _client!
                .ListObjectsV2Async(request)
                .GetAwaiter()
                .GetResult();

            foreach (S3Object obj in response.S3Objects)
                yield return (obj.Key, false);
            foreach (string cp in response.CommonPrefixes)
                yield return (cp, true);
            yield break;
        }

        StringBuilder qs = new();
        qs.Append("list-type=2");
        if (!string.IsNullOrEmpty(prefix))
            qs.Append("&prefix=").Append(Uri.EscapeDataString(prefix));
        if (!string.IsNullOrEmpty(delimiter))
            qs.Append("&delimiter=").Append(Uri.EscapeDataString(delimiter));
        qs.Append($"&max-keys={maxKeys}");

        string canonicalQs = BuildSortedQueryString(qs.ToString());

        (string authHeader, string amzDate) = S3SigV4.SignHeaderRequest(
            "GET",
            _endpoint!,
            _bucket,
            string.Empty,
            canonicalQs,
            _region!,
            _accessKey!,
            _secretKey!,
            DateTime.UtcNow
        );

        string listUrl =
            _endpoint!.TrimEnd('/') + "/" + Uri.EscapeDataString(_bucket) + "/?" + canonicalQs;

        using HttpRequestMessage req = new(HttpMethod.Get, listUrl);
        req.Headers.TryAddWithoutValidation("Authorization", authHeader);
        req.Headers.TryAddWithoutValidation(
            "x-amz-content-sha256",
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        );
        req.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        req.Headers.Host = S3SigV4.HostFromEndpoint(_endpoint!);

        using HttpResponseMessage res = Http.Send(req);
        if (!res.IsSuccessStatusCode)
        {
            string body = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            throw new IOException(
                $"S3 LIST '{listUrl}' failed: HTTP {(int)res.StatusCode} {res.ReasonPhrase}; body: {body}"
            );
        }

        string xml = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        (List<string> files, List<string> dirs, _) = ParseListXml(xml);

        foreach (string k in files)
            yield return (k, false);
        foreach (string d in dirs)
            yield return (d, true);
    }

    /// <summary>
    /// Returns raw S3 object keys from one page. Used for delete-directory / move.
    /// </summary>
    private IReadOnlyList<string> ListPageKeys(
        string prefix,
        string? delimiter,
        string? continuationToken,
        out string? nextContinuationToken
    )
    {
        if (!HasRawCredentials)
        {
            ListObjectsV2Request request = new()
            {
                BucketName = _bucket,
                Prefix = prefix,
                MaxKeys = 1000,
                Delimiter = delimiter,
                ContinuationToken = continuationToken,
            };
            ListObjectsV2Response response = _client!
                .ListObjectsV2Async(request)
                .GetAwaiter()
                .GetResult();
            nextContinuationToken =
                response.IsTruncated == true ? response.NextContinuationToken : null;
            return [.. response.S3Objects.Select(o => o.Key)];
        }

        (List<string> files, _, string? next) = ListPageRaw(
            prefix,
            delimiter,
            continuationToken,
            includeDirectoryMarkers: true
        );
        nextContinuationToken = next;
        return files;
    }

    // -----------------------------------------------------------------------
    // XML parsing
    // -----------------------------------------------------------------------

    private static (List<string> Files, List<string> Dirs, string? Next) ParseListXml(
        string xml,
        bool includeDirectoryMarkers = false
    )
    {
        XDocument doc = XDocument.Parse(xml);
        XNamespace ns = "http://s3.amazonaws.com/doc/2006-03-01/";

        List<string> files =
        [
            .. doc.Descendants(ns + "Contents")
                .Select(e => e.Element(ns + "Key")?.Value ?? string.Empty)
                .Where(k =>
                    !string.IsNullOrEmpty(k)
                    && (includeDirectoryMarkers || !k.EndsWith("/", StringComparison.Ordinal))
                ),
        ];

        List<string> dirs =
        [
            .. doc.Descendants(ns + "CommonPrefixes")
                .Select(e => e.Element(ns + "Prefix")?.Value ?? string.Empty)
                .Where(p => !string.IsNullOrEmpty(p)),
        ];

        string? next = doc.Descendants(ns + "NextContinuationToken").FirstOrDefault()?.Value;

        return (files, dirs, next);
    }

    private (
        List<(string Key, long Size, DateTime LastModified)> Files,
        List<string> Dirs,
        string? Next
    ) ListPageRawWithMeta(string prefix, string? delimiter, string? continuationToken)
    {
        StringBuilder qs = new();
        qs.Append("list-type=2");
        if (!string.IsNullOrEmpty(prefix))
            qs.Append("&prefix=").Append(Uri.EscapeDataString(prefix));
        if (!string.IsNullOrEmpty(delimiter))
            qs.Append("&delimiter=").Append(Uri.EscapeDataString(delimiter));
        qs.Append("&max-keys=1000");
        if (!string.IsNullOrEmpty(continuationToken))
            qs.Append("&continuation-token=").Append(Uri.EscapeDataString(continuationToken));

        string canonicalQs = BuildSortedQueryString(qs.ToString());

        (string authHeader, string amzDate) = S3SigV4.SignHeaderRequest(
            "GET",
            _endpoint!,
            _bucket,
            string.Empty,
            canonicalQs,
            _region!,
            _accessKey!,
            _secretKey!,
            DateTime.UtcNow
        );

        string listUrl =
            _endpoint!.TrimEnd('/') + "/" + Uri.EscapeDataString(_bucket) + "/?" + canonicalQs;

        using HttpRequestMessage req = new(HttpMethod.Get, listUrl);
        req.Headers.TryAddWithoutValidation("Authorization", authHeader);
        req.Headers.TryAddWithoutValidation(
            "x-amz-content-sha256",
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        );
        req.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        req.Headers.Host = S3SigV4.HostFromEndpoint(_endpoint!);

        using HttpResponseMessage res = Http.Send(req);
        if (!res.IsSuccessStatusCode)
        {
            string body = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            throw new IOException(
                $"S3 LIST '{listUrl}' failed: HTTP {(int)res.StatusCode} {res.ReasonPhrase}; body: {body}"
            );
        }

        string xml = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return ParseListXmlWithMeta(xml);
    }

    private static (
        List<(string Key, long Size, DateTime LastModified)> Files,
        List<string> Dirs,
        string? Next
    ) ParseListXmlWithMeta(string xml)
    {
        XDocument doc = XDocument.Parse(xml);
        XNamespace ns = "http://s3.amazonaws.com/doc/2006-03-01/";

        List<(string, long, DateTime)> files = [];
        foreach (XElement e in doc.Descendants(ns + "Contents"))
        {
            string key = e.Element(ns + "Key")?.Value ?? string.Empty;
            if (string.IsNullOrEmpty(key) || key.EndsWith("/", StringComparison.Ordinal))
                continue;

            long size = long.TryParse(e.Element(ns + "Size")?.Value, out long s) ? s : 0L;
            DateTime lastModified = DateTime.TryParse(
                e.Element(ns + "LastModified")?.Value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal
                    | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out DateTime lm
            )
                ? lm
                : DateTime.UtcNow;

            files.Add((key, size, lastModified));
        }

        List<string> dirs =
        [
            .. doc.Descendants(ns + "CommonPrefixes")
                .Select(e => e.Element(ns + "Prefix")?.Value ?? string.Empty)
                .Where(p => !string.IsNullOrEmpty(p)),
        ];

        string? next = doc.Descendants(ns + "NextContinuationToken").FirstOrDefault()?.Value;

        return (files, dirs, next);
    }

    // -----------------------------------------------------------------------
    // Query-string helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Takes a query string like "list-type=2&amp;prefix=foo&amp;delimiter=/"
    /// and returns it sorted by key name (required for SigV4 canonical form).
    /// </summary>
    private static string BuildSortedQueryString(string rawQs)
    {
        IEnumerable<string> parts = rawQs
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(p => p, StringComparer.Ordinal);
        return string.Join("&", parts);
    }

    // -----------------------------------------------------------------------
    // Dispose
    // -----------------------------------------------------------------------

    public void Dispose() => _client?.Dispose();

    // -----------------------------------------------------------------------
    // Pattern matching (glob → regex)
    // -----------------------------------------------------------------------
}
