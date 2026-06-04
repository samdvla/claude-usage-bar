# Builds both self-contained single-file exes into windows/dist.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root "dist"
$common = "-c", "Release", "-r", "win-x64", "--self-contained",
          "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
          "-o", $dist

dotnet publish (Join-Path $root "src/TrayApp/ClaudeUsageBar.csproj") @common
dotnet publish (Join-Path $root "src/Cli/Cli.csproj") @common
Write-Host "`nBuilt to $dist" -ForegroundColor Green
Get-ChildItem $dist -Filter *.exe | Select-Object Name, @{n="MB";e={[math]::Round($_.Length/1MB,1)}}
