param(
    [string]$Out = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw "csc.exe not found at $compiler" }

New-Item -ItemType Directory -Force -Path $Out | Out-Null
$output = Join-Path $Out 'CrossCrease.exe'
& $compiler /nologo /target:winexe /optimize+ "/out:$output" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
    /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    (Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'profiles') -Destination $Out -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $Out -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CHANGELOG.md') -Destination $Out -Force
Get-Item -LiteralPath $output | Select-Object FullName, Length, LastWriteTime
