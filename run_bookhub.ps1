$env:DOTNET_CLI_HOME = "$PSScriptRoot\.dotnet_home"
$env:NUGET_PACKAGES = "$PSScriptRoot\packages"
$env:NUGET_HTTP_CACHE_PATH = "$PSScriptRoot\packages\http-cache"
$env:NUGET_PLUGINS_CACHE_PATH = "$PSScriptRoot\packages\plugins-cache"
$env:TEMP = "$PSScriptRoot\.temp"
$env:TMP = "$PSScriptRoot\.temp"

New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:TEMP | Out-Null

Write-Host "Запуск BookHub (.NET 9 WPF) із локальними кешами на диску E:\..." -ForegroundColor Cyan
dotnet run --project "$PSScriptRoot\BookHub.App\BookHub.App.csproj"
