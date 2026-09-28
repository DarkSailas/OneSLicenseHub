# OneSLicenseHub build and publish script (.NET 10 / C# 14)
param (
    [string]$Configuration = "Release",
    [string]$OutputDir = "$PSScriptRoot\publish",
    [switch]$RunTests = $true,
    [switch]$SelfContained = $false
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " Building OneSLicenseHub (.NET 10 / C# 14)" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# 1. Restore
Write-Host "`n[1/4] Restoring NuGet packages..." -ForegroundColor Yellow
dotnet restore "$PSScriptRoot\OneSLicenseHub.slnx" --verbosity minimal

# 2. Build
Write-Host "`n[2/4] Compiling solution ($Configuration)..." -ForegroundColor Yellow
dotnet build "$PSScriptRoot\OneSLicenseHub.slnx" -c $Configuration --no-restore

# 3. Tests
if ($RunTests) {
    Write-Host "`n[3/4] Running unit tests..." -ForegroundColor Yellow
    dotnet test "$PSScriptRoot\tests\OneSLicenseHub.Tests\OneSLicenseHub.Tests.csproj" -c $Configuration --no-build --logger "console;verbosity=normal"
} else {
    Write-Host "`n[3/4] Skipping tests." -ForegroundColor DarkGray
}

# 4. Publish
Write-Host "`n[4/4] Publishing Web & Windows Service..." -ForegroundColor Yellow

$webPublishDir = Join-Path $OutputDir "Web"
if (-not (Test-Path $webPublishDir)) { New-Item -ItemType Directory -Path $webPublishDir -Force | Out-Null }

$webArgs = @("publish", "$PSScriptRoot\src\OneSLicenseHub.Web\OneSLicenseHub.Web.csproj", "-c", $Configuration, "-o", $webPublishDir, "--no-build")

if ($SelfContained) {
    $webArgs += "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true"
}

Write-Host "  -> Publishing OneSLicenseHub.Web to $webPublishDir..." -ForegroundColor DarkGray
dotnet @webArgs

Write-Host "`n==========================================" -ForegroundColor Green
Write-Host " Build and publish completed successfully!" -ForegroundColor Green
Write-Host " Output Directory: $webPublishDir" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
