param(
    [string]$Out = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw "csc.exe not found at $compiler" }

New-Item -ItemType Directory -Force -Path $Out | Out-Null
$output = Join-Path $Out 'CrossCrease.exe'
$logo = Join-Path $PSScriptRoot 'assets\crosscrease-wordmark.png'
if (-not (Test-Path -LiteralPath $logo)) { throw "Logo not found at $logo" }
$icon = Join-Path $PSScriptRoot 'assets\crosscrease.ico'
if (-not (Test-Path -LiteralPath $icon)) { throw "Icon not found at $icon" }
& $compiler /nologo /target:winexe /optimize+ "/out:$output" "/win32icon:$icon" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Security.dll `
    /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    "/resource:$logo,CrossCrease.TextLogo.png" `
    "/resource:$icon,CrossCrease.Icon.ico" `
    (Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'profiles') -Destination $Out -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $Out -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CHANGELOG.md') -Destination $Out -Force
Get-Item -LiteralPath $output | Select-Object FullName, Length, LastWriteTime
