param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [switch]$ReaderOnly
)

$ErrorActionPreference = "Stop"

$appProject = Join-Path $PSScriptRoot "BookHub.App\BookHub.App.csproj"
$readerProject = Join-Path $PSScriptRoot "AllReaderBook\AllReaderBook.csproj"
$payloadDir = Join-Path $PSScriptRoot "Publish\ReaderHub_InstallerPayload_v3.65"
$readerPayloadDir = Join-Path $PSScriptRoot "Publish\AllReaderBook_InstallerPayload_v0.9.02-beta"
$installerSettingsDir = Join-Path $PSScriptRoot "Publish\ReaderHub_InstallerSettings_v3.65"

if (-not (Test-Path $readerProject)) {
    throw "Reader project is missing: $readerProject"
}

$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot ".dotnet_home"
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot "packages"
$env:NUGET_HTTP_CACHE_PATH = Join-Path $env:NUGET_PACKAGES "http-cache"
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $env:NUGET_PACKAGES "plugins-cache"
$env:TEMP = Join-Path $PSScriptRoot ".temp"
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:TEMP, $payloadDir, $readerPayloadDir, $installerSettingsDir | Out-Null

$pdfium = Get-ChildItem (Join-Path $PSScriptRoot "packages\bblanchon.pdfium.win32") -Filter "pdfium.dll" -Recurse |
    Where-Object { $_.FullName -match "\\runtimes\\$([regex]::Escape($Runtime))\\native\\pdfium\.dll$" } |
    Select-Object -First 1
if ($null -eq $pdfium) {
    throw "Could not find the PDFium native dependency for runtime $Runtime."
}

$defaultReaderSettings = @"
{
  "WindowWidth": 1360,
  "WindowHeight": 880,
  "WindowLeft": "NaN",
  "WindowTop": "NaN",
  "WindowState": "Maximized",
  "TocWidth": 285,
  "NotesWidth": 320,
  "IsTocVisible": true,
  "IsAnnotationsVisible": false,
  "ZoomMode": "FitWidth",
  "LastReaderViewMode": "ContinuousVertical",
  "ThemeName": "Obsidian Dark",
  "Language": "uk",
  "IsLicenseAccepted": false,
  "IsSmartPdfColorFilterEnabled": true,
  "RestorePreviousSessionOnStartup": true,
  "RecentFiles": [],
  "OpenSessionTabs": []
}
"@

# 1. Publish Standalone AllReaderBook Installer Payload (ReadyToRun)
Write-Host "Publishing standalone AllReaderBook ($Runtime, ReadyToRun) to $readerPayloadDir" -ForegroundColor Cyan
Get-ChildItem -Path $readerPayloadDir -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
& dotnet publish $readerProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $readerPayloadDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish AllReaderBook failed with exit code $LASTEXITCODE"
}
Copy-Item $pdfium.FullName (Join-Path $readerPayloadDir "pdfium.dll") -Force
[System.IO.File]::WriteAllText(
    (Join-Path $readerPayloadDir "allreaderbook_settings.json"),
    $defaultReaderSettings,
    [System.Text.UTF8Encoding]::new($false))

if (-not $ReaderOnly) {
    # 2. Publish Combined ReaderHub Suite Payload (ReaderHub.exe + AllReaderBook.exe)
    Write-Host "Publishing self-contained ReaderHub Suite ($Runtime) to $payloadDir" -ForegroundColor Cyan
    Get-ChildItem -Path $payloadDir -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
    & dotnet publish $readerProject `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishReadyToRun=true `
        -p:PublishSingleFile=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $payloadDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish AllReaderBook (suite) failed with exit code $LASTEXITCODE"
    }

    & dotnet publish $appProject `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $payloadDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish ReaderHub failed with exit code $LASTEXITCODE"
    }

    foreach ($staleFileName in @("bookhub.db", "bookhub.db-shm", "bookhub.db-wal", "settings.json", "allreaderbook_settings.json", "reader_settings.json", ".bookhub-install-rebase", "BookHub.Reader.exe", "BookHub.Reader.dll", "BookHub.App.exe", "BookHub.App.dll")) {
        $staleFilePath = Join-Path $payloadDir $staleFileName
        if (Test-Path $staleFilePath -PathType Leaf) {
            Remove-Item -LiteralPath $staleFilePath -Force
        }
    }
    foreach ($staleDirectoryName in @("cache", "Backups", "libofbooks", ".temp", "SampleLibrary")) {
        $staleDirectoryPath = Join-Path $payloadDir $staleDirectoryName
        if (Test-Path $staleDirectoryPath -PathType Container) {
            Remove-Item -LiteralPath $staleDirectoryPath -Recurse -Force
        }
    }

    $databaseInitialization = Start-Process `
        -FilePath (Join-Path $payloadDir "ReaderHub.exe") `
        -ArgumentList "--initialize-database" `
        -WorkingDirectory "C:\Windows\System32" `
        -Wait `
        -PassThru
    if ($databaseInitialization.ExitCode -ne 0) {
        throw "Could not create a clean installer database (exit code $($databaseInitialization.ExitCode))."
    }

    Copy-Item $pdfium.FullName (Join-Path $payloadDir "pdfium.dll") -Force

    $settingsJson = "{`r`n  `"IsFirstRun`": true`r`n}"
    [System.IO.File]::WriteAllText(
        (Join-Path $installerSettingsDir "settings.json"),
        $settingsJson,
        [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText(
        (Join-Path $installerSettingsDir "allreaderbook_settings.json"),
        $defaultReaderSettings,
        [System.Text.UTF8Encoding]::new($false))

    foreach ($requiredPayloadFile in @(
        "ReaderHub.exe",
        "AllReaderBook.exe",
        "ReaderHub.dll",
        "AllReaderBook.dll",
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
    Write-Host "[WARN] Inno Setup 6 (ISCC.exe) not found on PATH; payloads are ready in Publish\." -ForegroundColor Yellow
    return
}

$outputDir = Join-Path $PSScriptRoot "Publish\Installer"
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
Push-Location $PSScriptRoot
try {
    $readerIss = Join-Path $PSScriptRoot "AllReaderBook\installer_allreaderbook.iss"
    if (Test-Path $readerIss) {
        Push-Location (Join-Path $PSScriptRoot "AllReaderBook")
        try {
            & $compiler "/Qp" "installer_allreaderbook.iss"
            if ($LASTEXITCODE -ne 0) {
                throw "Inno Setup compilation for installer_allreaderbook.iss failed with exit code $LASTEXITCODE"
            }
            Write-Host "[SUCCESS] Standalone AllReaderBook Installer created in AllReaderBook\Publish\Installer" -ForegroundColor Green
        }
        finally {
            Pop-Location
        }
    }

    if (-not $ReaderOnly) {
        & $compiler "/Qp" "installer.iss"
        if ($LASTEXITCODE -ne 0) {
            throw "Inno Setup compilation for installer.iss failed with exit code $LASTEXITCODE"
        }
        Write-Host "[SUCCESS] ReaderHub Suite Installer created: $(Join-Path $outputDir 'ReaderHub_Setup_v3.65_x64.exe')" -ForegroundColor Green
    }
}
finally {
    Pop-Location
}
