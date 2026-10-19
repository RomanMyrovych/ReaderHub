<#
.SYNOPSIS
    BookHub Pro v3.5 Portable Publisher (SingleFolder on E:\ or D:\, zero C:\ drive usage)
#>

param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$OutputDir = "$PSScriptRoot\Publish\BookHub_Portable_v3.5",
    [switch]$IncludeSampleLibrary
)

$ErrorActionPreference = "Stop"

# 1. Isolate all CLI, NuGet and TEMP directories strictly on E:\ / D:\
$env:DOTNET_CLI_HOME = "$PSScriptRoot\.dotnet_home"
$env:NUGET_PACKAGES = "$PSScriptRoot\packages"
$env:NUGET_HTTP_CACHE_PATH = "$PSScriptRoot\packages\http-cache"
$env:NUGET_PLUGINS_CACHE_PATH = "$PSScriptRoot\packages\plugins-cache"
$env:TEMP = "$PSScriptRoot\.temp"
$env:TMP = "$PSScriptRoot\.temp"

New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:TEMP, $OutputDir | Out-Null

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Building BookHub Pro v3.5 Portable ($Runtime)" -ForegroundColor Cyan
Write-Host " Output Directory: $OutputDir" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 2. Publish WPF application (--self-contained false -p:PublishSingleFile=false)
dotnet publish "$PSScriptRoot\BookHub.App\BookHub.App.csproj" `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $OutputDir

# 3. Copy native pdfium.dll to portable root
$pdfiumCandidate = Get-ChildItem -Path "$PSScriptRoot\packages\bblanchon.pdfium.win32" -Filter "pdfium.dll" -Recurse |
    Where-Object { $_.FullName -match $Runtime } |
    Select-Object -First 1

if ($null -ne $pdfiumCandidate) {
    Copy-Item -Path $pdfiumCandidate.FullName -Destination (Join-Path $OutputDir "pdfium.dll") -Force
    Write-Host "[OK] Copied native PDFium engine: $($pdfiumCandidate.FullName)" -ForegroundColor Green
}

# 4. Copy MIT LICENSE and THIRD_PARTY_LICENSES.txt
foreach ($docFile in @("LICENSE", "THIRD_PARTY_LICENSES.txt")) {
    $srcDoc = Join-Path $PSScriptRoot $docFile
    if (Test-Path $srcDoc) {
        Copy-Item -Path $srcDoc -Destination (Join-Path $OutputDir $docFile) -Force
        Write-Host "[OK] Included legal notice file: $docFile" -ForegroundColor Green
    }
}

# 5. Create local cache & Backups folders. bookhub.db and settings.json are NOT copied from the
#    dev tree on purpose: the app now provisions them next to the executable on first launch
#    (schema is created via CREATE TABLE IF NOT EXISTS), so the portable folder stays clean and
#    is fully self-contained even when started from a Desktop shortcut.
New-Item -ItemType Directory -Force -Path (Join-Path $OutputDir "cache"), (Join-Path $OutputDir "Backups") | Out-Null

# Optional: seed a pre-built empty database template if one is provided in the repo root.
if (Test-Path "$PSScriptRoot\bookhub.template.db") {
    Copy-Item -Path "$PSScriptRoot\bookhub.template.db" -Destination (Join-Path $OutputDir "bookhub.db") -Force
    Write-Host "[OK] Seeded empty database template bookhub.db" -ForegroundColor Green
}

if ($IncludeSampleLibrary -and (Test-Path "$PSScriptRoot\SampleLibrary")) {
    $destSample = Join-Path $OutputDir "SampleLibrary"
    New-Item -ItemType Directory -Force -Path $destSample | Out-Null
    Copy-Item -Path "$PSScriptRoot\SampleLibrary\*" -Destination $destSample -Recurse -Force
    Write-Host "[OK] Included SampleLibrary demo collection" -ForegroundColor Green
}

Write-Host ""
Write-Host "[SUCCESS] BookHub_Portable_v3.5 created at:" -ForegroundColor Green
Write-Host "   $OutputDir" -ForegroundColor Yellow
