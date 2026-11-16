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

using System.Management;
using NoMercy.NmSystem.SystemCalls;

namespace NoMercy.NmSystem.Information;

public static class Gpu
{
    public static List<string> Vendors()
    {
        if (Software.IsWindows)
            return GetGpuVendorsWindows();

        if (Software.IsLinux)
            return GetGpuVendorsLinux();

        if (Software.IsMac)
            return GetGpuVendorsMac();

        return ["Unknown"];
    }

    private static List<string> GetGpuVendorsWindows()
    {
        List<string> vendors = [];
#pragma warning disable CA1416
        try
        {
            using ManagementObjectSearcher searcher = new("SELECT * FROM Win32_VideoController");

            foreach (ManagementBaseObject? obj in searcher.Get())
            {
                string vendor = obj["AdapterCompatibility"]?.ToString() ?? "Unknown";
                if (!vendors.Contains(vendor))
                    vendors.Add(vendor);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Error detecting GPUs on Windows: {ex.Message}");
        }
#pragma warning restore CA1416
        return vendors;
    }

    private static List<string> GetGpuVendorsLinux()
    {
        string output = Shell.ExecCommand("lspci | grep -i 'VGA' | awk -F ': ' '{print $2}'");
        return output.Split('\n').Where(v => !string.IsNullOrEmpty(v)).ToList();
    }

    private static List<string> GetGpuVendorsMac()
    {
        string output = Shell.ExecCommand(
            "system_profiler SPDisplaysDataType | grep 'Chipset Model' | awk -F ': ' '{print $2}'"
        );
        return output.Split('\n').Where(v => !string.IsNullOrEmpty(v)).ToList();
    }

    public static List<string> Names()
    {
        if (Software.IsWindows)
            return GetGpuNamesWindows();

        if (Software.IsLinux)
            return GetGpuNamesLinux();

        if (Software.IsMac)
            return GetGpuNamesMac();

        return ["Unknown"];
    }

    private static List<string> GetGpuNamesWindows()
    {
        List<string> gpus = [];

#pragma warning disable CA1416
        try
        {
            ManagementObjectSearcher searcher = new("select Name from Win32_VideoController");
            foreach (ManagementBaseObject? o in searcher.Get())
            {
                if (o is not ManagementObject item)
                    continue;
                if (item["Name"] is not { } value)
                    continue;
                if (value.ToString() is not { } valueString)
                    continue;
                gpus.Add(valueString.Trim());
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Error detecting GPUs on Windows: {ex.Message}");
        }
#pragma warning restore CA1416

        return gpus;
    }

    private static List<string> GetGpuNamesLinux()
    {
        string output = Shell.ExecCommand("lspci | grep 'VGA'");
        return output.Split('\n').Where(v => !string.IsNullOrEmpty(v)).Select(LinuxDisplayName).ToList();
    }

    private static readonly (string Vendor, string Short)[] LinuxVendors =
    [
        ("Advanced Micro Devices, Inc. [AMD/ATI]", "AMD"),
        ("Advanced Micro Devices, Inc.", "AMD"),
        ("NVIDIA Corporation", "NVIDIA"),
        ("Intel Corporation", "Intel"),
        ("Matrox Electronics Systems Ltd.", "Matrox"),
    ];

    // lspci prints "<slot> <class>: <vendor> <chip> [<model>] (rev xx)"; Windows and Mac give only vendor and model.
    public static string LinuxDisplayName(string lspciLine)
    {
        string name = lspciLine.Trim();
        int space = name.IndexOf(' ');
        int colon = name.IndexOf(": ", StringComparison.Ordinal);
        if (space > 0 && colon > space && IsPciSlot(name[..space]))
            name = name[(colon + 2)..].Trim();

        int rev = name.LastIndexOf(" (rev ", StringComparison.Ordinal);
        if (rev > 0 && name.EndsWith(')'))
            name = name[..rev].TrimEnd();

        foreach ((string vendor, string shortName) in LinuxVendors)
        {
            if (!name.StartsWith(vendor + " ", StringComparison.Ordinal))
                continue;
            string model = name[vendor.Length..].Trim();
            int open = model.LastIndexOf('[');
            if (open >= 0 && model.EndsWith(']'))
                model = model[(open + 1)..^1].Trim();
            return $"{shortName} {model}";
        }

        return name;
    }

    private static bool IsPciSlot(string token) =>
        token.Contains(':') && token.Contains('.') && token.All(c => char.IsAsciiHexDigit(c) || c is ':' or '.');

    private static List<string> GetGpuNamesMac()
    {
        List<string> gpus = [];

        string systemProfilerOutput = Shell.ExecCommand("system_profiler SPDisplaysDataType");

        string[] lines = systemProfilerOutput.Split('\n');
        foreach (string line in lines)
        {
            const string marker = "Chipset Model:";
            if (line.TrimStart().StartsWith(marker))
            {
                string gpuName = line.Substring(
                        line.IndexOf(marker, StringComparison.Ordinal) + marker.Length
                    )
                    .Trim();
                if (!string.IsNullOrEmpty(gpuName))
                    gpus.Add(gpuName);
            }
        }

        return gpus;
    }
}
