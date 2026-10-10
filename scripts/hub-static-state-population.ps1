$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$paths = @(
    (Join-Path $root 'src/NoMercy.Api/Hubs')
    (Join-Path $root 'src/NoMercy.Networking/ConnectionHub.cs')
    (Join-Path $root 'src/NoMercy.Api/Services/Music')
    (Join-Path $root 'src/NoMercy.Api/Services/Video')
    (Join-Path $root 'src/NoMercy.Networking/Messaging/ConnectedClients.cs')
    (Join-Path $root 'src/NoMercy.Api/WebSockets/DeviceBusRegistry.cs')
    (Join-Path $root 'src/NoMercy.Encoder/Devices/DeviceCapabilityRegistry.cs')
    (Join-Path $root 'src/NoMercy.Encoder/LiveTranscode/LiveSessionPresenceTracker.cs')
)

$files = foreach ($path in $paths) {
    if (Test-Path $path -PathType Container) {
        Get-ChildItem $path -Filter '*.cs' -Recurse -File
    } else {
        Get-Item $path
    }
}

$candidates = foreach ($file in $files) {
    Select-String -Path $file.FullName -Pattern '^\s*(private|public|internal|protected)\s+static\s+(readonly\s+)?[^\(]*(=|;)' |
        Where-Object { $_.Line -notmatch '\b(const|static\s+\w+\s+\w+\s*\()\b' } |
        ForEach-Object {
            [pscustomobject]@{
                File = $file.FullName.Substring($root.Length + 1)
                Line = $_.LineNumber
                Declaration = $_.Line.Trim()
            }
        }
}

$hubFiles = Get-ChildItem (Join-Path $root 'src/NoMercy.Api/Hubs') -Filter '*.cs' -File |
    Where-Object { (Get-Content $_.FullName -Raw) -match ':\s*ConnectionHub\b' }

$serviceCollections = foreach ($file in $files) {
    if ($file.FullName -match '[\\/]Hubs[\\/]') {
        continue
    }
    Select-String -Path $file.FullName -Pattern '^\s*(private|public|internal|protected)\s+(readonly\s+)?ConcurrentDictionary' |
        ForEach-Object {
            [pscustomobject]@{
                File = $file.FullName.Substring($root.Length + 1)
                Line = $_.LineNumber
                Declaration = $_.Line.Trim()
            }
        }
}

foreach ($candidate in $candidates) {
    Write-Output ('{0}:{1}: {2}' -f $candidate.File, $candidate.Line, $candidate.Declaration)
}
Write-Output "Hub subclasses: $(@($hubFiles).Count)"
Write-Output "Static field candidates: $(@($candidates).Count)"
foreach ($collection in $serviceCollections) {
    Write-Output ('{0}:{1}: {2}' -f $collection.File, $collection.Line, $collection.Declaration)
}
Write-Output "Shared service collection candidates: $(@($serviceCollections).Count)"
