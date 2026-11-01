$ErrorActionPreference = "Stop"

$ProjectDir = Get-Location
$ProjectFile = "BrutalSandbox.csproj"
$OutputBaseDir = Join-Path $ProjectDir "build_releases"

Write-Host "====================================================" -ForegroundColor Cyan
Write-Host "build settings" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan

$BuildVersion = Read-Host "Build Version? [Press Enter to set 1.0.0.1]"
if ([string]::IsNullOrWhiteSpace($BuildVersion)) {
    $BuildVersion = "1.0.0.1"
}
if (Test-Path $OutputBaseDir) { 
    Remove-Item $OutputBaseDir -Recurse -Force 
}
New-Item -ItemType Directory -Path $OutputBaseDir | Out-Null

Write-Host "`n====================================================" -ForegroundColor Cyan
Write-Host " building: $BuildVersion" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan

$CommonArgs = "-c Release --self-contained true /p:PublishSingleFile=true /p:PublishTrimmed=true /p:TrimMode=partial /p:DebugType=none /p:DebugSymbols=false /p:Version=$BuildVersion /p:AssemblyVersion=$BuildVersion /p:FileVersion=$BuildVersion"

Write-Host "`n[+] Compiling to: win-x64..." -ForegroundColor Green
$PublishDirWin = Join-Path $ProjectDir "bin\Release\net10.0\win-x64\publish"

Invoke-Expression "dotnet publish $ProjectFile -r win-x64 $CommonArgs"

if (Test-Path $PublishDirWin) {
    Write-Host "    Windows..." -ForegroundColor Yellow
    $ZipPathWin = Join-Path $OutputBaseDir "BrutalSandbox_v$BuildVersion`_win-x64.zip"
    Compress-Archive -Path "$PublishDirWin\*" -DestinationPath $ZipPathWin -Force
    Write-Host "    [ OK ]: $ZipPathWin" -ForegroundColor Green
}

Write-Host "`n[+] Compiling to: linux-x64..." -ForegroundColor Green
$PublishDirLin = Join-Path $ProjectDir "bin\Release\net10.0\linux-x64\publish"

Invoke-Expression "dotnet publish $ProjectFile -r linux-x64 $CommonArgs"

if (Test-Path $PublishDirLin) {
    Write-Host "    Linux..." -ForegroundColor Yellow
    $TarGzPath = Join-Path $OutputBaseDir "BrutalSandbox_v$BuildVersion`_linux-x64.tar.gz"
    
    Push-Location $PublishDirLin
    tar -czf $TarGzPath *
    Pop-Location
    
    Write-Host "    [ OK ]: $TarGzPath" -ForegroundColor Green
}

Write-Host "`n[+] Compiling to: osx-arm64..." -ForegroundColor Green
$PublishDirMac = Join-Path $ProjectDir "bin\Release\net10.0\osx-arm64\publish"

Invoke-Expression "dotnet publish $ProjectFile -r osx-arm64 $CommonArgs"

if (Test-Path $PublishDirMac) {
    Write-Host "    macOS..." -ForegroundColor Yellow
    $ZipPathMac = Join-Path $OutputBaseDir "BrutalSandbox_v$BuildVersion`_osx-arm64.zip"
    Compress-Archive -Path "$PublishDirMac\*" -DestinationPath $ZipPathMac -Force
    Write-Host "    [ OK ]: $ZipPathMac" -ForegroundColor Green
}

Write-Host "`n====================================================" -ForegroundColor Cyan
Write-Host " script done $BuildVersion in: $OutputBaseDir" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan
