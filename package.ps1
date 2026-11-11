# Builds the launcher and produces its release assets in artifacts\<version>\:
#   crosscrease-launcher-<version>-windows-x64.zip   CrossCrease.exe, profiles, README, CHANGELOG
#   crosscrease-launcher-feed.json                   read by every launcher at startup to offer updates
#   SHA256SUMS.txt
# Upload all three unchanged as the assets of GitHub release v<version> in CrossCrease/launcher.
param(
    [string]$Repository = 'CrossCrease/launcher'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$dist = Join-Path $PSScriptRoot 'dist'
if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
& (Join-Path $PSScriptRoot 'build.ps1') -Out $dist | Out-Null

$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'CrossCrease.cs') -Raw
if ($source -notmatch 'public const string Version = "([^"]+)"') { throw 'BuildInfo.Version not found.' }
$version = $Matches[1]
$out = Join-Path $PSScriptRoot "artifacts\$version"
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Path $out -Force | Out-Null

$zipName = "crosscrease-launcher-$version-windows-x64.zip"
$zip = Join-Path $out $zipName
$base = (Resolve-Path -LiteralPath $dist).Path.TrimEnd('\') + '\'
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $dist -Recurse -File | Sort-Object FullName) {
        $name = 'CrossCrease/' + $file.FullName.Substring($base.Length).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $archive.Dispose() }

$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $zip).Length
[ordered]@{
    schema_version = 1
    game = 'launcher'
    release = $version
    prerelease = $version.Contains('-')
    published = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
    notes_url = "https://github.com/$Repository/releases/tag/v$version"
    source_url = "https://github.com/$Repository/tree/v$version"
    packages = @([ordered]@{
        id = 'launcher'; name = 'CrossCrease launcher'; version = $version
        description = 'Game-agnostic CrossCrease launcher.'
        url = $zipName; size = $size; sha256 = $hash
    })
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $out 'crosscrease-launcher-feed.json') -Encoding ASCII
"$hash  $zipName" | Set-Content -LiteralPath (Join-Path $out 'SHA256SUMS.txt') -Encoding ASCII
Get-ChildItem -LiteralPath $out | Select-Object Name, Length
