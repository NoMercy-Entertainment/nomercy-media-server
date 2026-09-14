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
using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using MimeMapping;
using NoMercy.NmSystem.Monitoring;
using NoMercy.Storage;

namespace NoMercy.Api.Middleware;

/// <summary>
/// Folder routing handle: maps a folder ULID to the driver instance + sub-path
/// the file lives under. Resolved per-request through IStorageFactory so NFS,
/// S3, WebDAV and local backends all stream through the same path.
/// </summary>
public readonly record struct FolderRef(Ulid DriverId, string SubPath);
