$files = @(
  'C:/Projects/NoMercy/server/nomercy-media-server/tests/NoMercy.Tests.Networking/NetworkingExternalIpTests.cs',
  'C:/Projects/NoMercy/server/nomercy-media-server/src/NoMercy.Setup/DesktopIconCreator.cs',
  'C:/Projects/NoMercy/server/nomercy-media-server/src/NoMercy.Setup/OfflineJwksCache.cs',
  'C:/Projects/NoMercy/server/nomercy-media-server/src/NoMercy.Setup/ApiInfo.cs'
)

foreach ($f in $files) {
    if (-not (Test-Path $f)) { Write-Host "Not found: $f"; continue }
    $content = [System.IO.File]::ReadAllText($f, [System.Text.Encoding]::UTF8)
    if ($content -match 'LocalStorageDriver' -and $content -notmatch 'using NoMercy\.Storage\.Drivers\.Local') {
        if ($content -match 'using NoMercy\.Storage;') {
            $newLine = [System.Environment]::NewLine
            $updated = $content.Replace('using NoMercy.Storage;', "using NoMercy.Storage;" + $newLine + "using NoMercy.Storage.Drivers.Local;")
        } else {
            # Find the last 'using ...' line and insert after it
            $lines = $content -split "`n"
            $lastUsing = -1
            for ($i = 0; $i -lt $lines.Length; $i++) {
                if ($lines[$i] -match '^\s*using ') { $lastUsing = $i }
            }
            if ($lastUsing -ge 0) {
                $linesList = [System.Collections.Generic.List[string]]::new($lines)
                $linesList.Insert($lastUsing + 1, "using NoMercy.Storage.Drivers.Local;")
                $updated = [string]::Join("`n", $linesList)
            } else {
                Write-Host "No using anchor in: $f"; continue
            }
        }
        [System.IO.File]::WriteAllText($f, $updated, [System.Text.Encoding]::UTF8)
        Write-Host "Updated: $f"
    } else {
        Write-Host "No changes needed: $f"
    }
}
