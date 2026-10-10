$root = (Resolve-Path (Join-Path $PSScriptRoot '..\src\NoMercy.Providers')).Path
$limits = @{
    Abstractions = 'per provider'; Helpers = '1 / 1000ms'
    AcoustId = '3 / 1000ms'; AniList = '1 / 2000ms'; CoverArt = '3 / 1000ms'
    FanArt = '3 / 1000ms'; Jikan = '1 / 350ms'; Lrclib = '1 / 1000ms'
    MusicBrainz = '1 / 1500ms'; MusixMatch = '2 / 1000ms'; NoMercy = '50 / 1000ms'
    OpenSubtitles = '1 / 1000ms'; Tadb = '2 / 1000ms'; TMDB = '50 / 1000ms'
    TVDB = '50 / 1000ms'
}
$files = Get-ChildItem $root -Recurse -Filter '*.cs' | Where-Object { $_.FullName -notmatch '[\\/](Models|obj|bin)[\\/]' }
$rows = foreach ($file in $files) {
    $source = Get-Content $file.FullName -Raw
    if ($source -match 'public\s+(?:(?:abstract|static|sealed)\s+)?class\s+(?<name>\w*(?:Client|MetadataProvider))\b') {
        $name = $Matches.name
        $provider = $file.FullName.Substring($root.Length + 1).Split([IO.Path]::DirectorySeparatorChar)[0]
        $special = switch ($name) {
            'TvdbBaseClient' { 'TVDB queue + direct TvdbLogin HttpClient'; break }
            'ExternalApiClient' { 'named provider queue (base class)'; break }
            'BaseClient' { 'General queue (legacy base class)'; break }
            'TmdbImageClient' { 'TmdbImage queue'; break }
            'NoMercyImageClient' { 'NoMercyImage queue'; break }
            { $_ -in 'FanArtImageClient', 'CoverArtCoverArtClient' } { 'API queue + direct image HttpClient'; break }
            { $_ -in 'AniListClient', 'JikanClient' } { 'caller supplied HttpClient; no own queue'; break }
            default { $null }
        }
        $transport = if ($special) { $special } elseif ($name -like '*MetadataProvider' -and $provider -eq 'TMDB') { 'delegates to TMDB client' } else { "$provider shared queue (inherited)" }
        $limit = $limits[$provider]
        if ($name -in 'FanArtImageClient', 'CoverArtCoverArtClient', 'TvdbBaseClient') { $limit += '; direct path none' }
        if ($name -in 'AniListClient', 'JikanClient') { $limit = 'none direct; wrapper uses provider limit' }
        $retry = if ($transport -like 'delegates*') { 'inherited' } elseif ($transport -like 'caller supplied*') { 'none; error becomes null' } else { '429/502/503/504/403: 2/4/8s; no jitter or Retry-After' }
        if ($name -in 'FanArtImageClient', 'CoverArtCoverArtClient', 'TvdbBaseClient') { $retry += '; direct path: none' }
        if ($name -in 'TmdbImageClient', 'NoMercyImageClient') { $retry = 'queue exists; HTTP failures return null before retry' }
        if ($name -in 'AniListMetadataProvider', 'JikanMetadataProvider') { $retry = 'queue exists; HTTP failures return null before retry' }
        [pscustomobject]@{ Provider = $provider; Client = $name; Transport = $transport; Limit = $limit; Retry = $retry }
    }
}
$rows = @($rows | Sort-Object Provider, Client)
"Count: $($rows.Count)"
'| Provider | Client | Before: queue / HttpClient | Before: cap / batch interval | Before: retry |'
'| --- | --- | --- | --- | --- |'
$rows | ForEach-Object { "| $($_.Provider) | $($_.Client) | $($_.Transport) | $($_.Limit) | $($_.Retry) |" }
