param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$appProject = Join-Path $PSScriptRoot "BookHub.App\BookHub.App.csproj"
$payloadDir = Join-Path $PSScriptRoot "Publish\BookHub_InstallerPayload_v3.5.1"
$installerSettingsDir = Join-Path $PSScriptRoot "Publish\BookHub_InstallerSettings_v3.5.1"

if (-not (Test-Path $appProject)) {
    throw "Application project is missing: $appProject"
}

$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot ".dotnet_home"
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot "packages"
$env:NUGET_HTTP_CACHE_PATH = Join-Path $env:NUGET_PACKAGES "http-cache"
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $env:NUGET_PACKAGES "plugins-cache"
$env:TEMP = Join-Path $PSScriptRoot ".temp"
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:TEMP, $payloadDir | Out-Null

Write-Host "Publishing self-contained $Runtime release to $payloadDir" -ForegroundColor Cyan
& dotnet publish $appProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $payloadDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$stagingRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "Publish"))
$payloadFullPath = [System.IO.Path]::GetFullPath($payloadDir)
if (-not $payloadFullPath.StartsWith($stagingRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean installer payload outside the Publish directory: $payloadFullPath"
}

foreach ($staleFileName in @("bookhub.db", "bookhub.db-shm", "bookhub.db-wal", "settings.json", ".bookhub-install-rebase")) {
    $staleFilePath = Join-Path $payloadDir $staleFileName
    if (Test-Path $staleFilePath -PathType Leaf) {
        Remove-Item -LiteralPath $staleFilePath -Force
    }
}
foreach ($staleDirectoryName in @("cache", "Backups", ".temp", "SampleLibrary")) {
    $staleDirectoryPath = Join-Path $payloadDir $staleDirectoryName
    if (Test-Path $staleDirectoryPath -PathType Container) {
        Remove-Item -LiteralPath $staleDirectoryPath -Recurse -Force
    }
}

$databaseInitialization = Start-Process `
    -FilePath (Join-Path $payloadDir "BookHub.App.exe") `
    -ArgumentList "--initialize-database" `
    -WorkingDirectory "C:\Windows\System32" `
    -Wait `
    -PassThru
if ($databaseInitialization.ExitCode -ne 0) {
    throw "Could not create a clean installer database (exit code $($databaseInitialization.ExitCode))."
}
if (-not (Test-Path (Join-Path $payloadDir "bookhub.db"))) {
    throw "Database initialization did not create bookhub.db in the installer payload."
}

$pdfium = Get-ChildItem (Join-Path $PSScriptRoot "packages\bblanchon.pdfium.win32") -Filter "pdfium.dll" -Recurse |
    Where-Object { $_.FullName -match "\\runtimes\\$([regex]::Escape($Runtime))\\native\\pdfium\.dll$" } |
    Select-Object -First 1
if ($null -eq $pdfium) {
    throw "Could not find the PDFium native dependency for runtime $Runtime."
}
Copy-Item $pdfium.FullName (Join-Path $payloadDir "pdfium.dll") -Force

New-Item -ItemType Directory -Force -Path $installerSettingsDir | Out-Null
$settingsJson = "{`r`n  `"IsFirstRun`": true`r`n}"
[System.IO.File]::WriteAllText(
    (Join-Path $installerSettingsDir "settings.json"),
    $settingsJson,
    [System.Text.UTF8Encoding]::new($false))

foreach ($requiredPayloadFile in @(
    "BookHub.App.exe",
    "BookHub.App.dll",
    "hostfxr.dll",
    "coreclr.dll",
    "pdfium.dll",
    "e_sqlite3.dll"
)) {
    $path = Join-Path $payloadDir $requiredPayloadFile
    if (-not (Test-Path $path)) {
        throw "Self-contained installer payload is incomplete: $requiredPayloadFile was not published."
    }
}

$compilerCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
)
$compiler = (Get-Command "ISCC.exe" -ErrorAction SilentlyContinue).Source
if (-not $compiler) {
    $compiler = $compilerCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $compiler) {
    throw "Inno Setup 6 (ISCC.exe) is required to compile installer. Install it and run this script again."
}

$outputDir = Join-Path $PSScriptRoot "Publish\Installer"
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
Push-Location $PSScriptRoot
try {
    & $compiler "/Qp" "installer.iss"
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compilation failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$setupPath = Join-Path $outputDir "BookHubPro_Setup_v3.5_x64.exe"
if (-not (Test-Path $setupPath)) {
    throw "Inno Setup completed without creating the expected installer: $setupPath"
}

Write-Host "[SUCCESS] Installer created: $setupPath" -ForegroundColor Green
