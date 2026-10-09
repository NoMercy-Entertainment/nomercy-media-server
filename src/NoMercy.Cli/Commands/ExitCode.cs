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
namespace NoMercy.Cli.Commands;

/// <summary>
/// Process exit codes returned by the CLI commands. Using a named enum instead
/// of bare integers makes each command's success/failure result explicit and
/// consistent. Zero is success; every non-zero value signals a distinct class
/// of failure.
/// </summary>
internal enum ExitCode
{
    Success = 0,
    ConfigurationError = 1,
    ConnectionError = 2,
    ServerError = 3,
    Timeout = 4,
    RollbackFailed = 5,
}
