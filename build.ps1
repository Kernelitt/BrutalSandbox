# Скрипт автоматической кроссплатформенной сборки и упаковки релиза BrutalSandbox с версионированием
$ErrorActionPreference = "Stop"

# Получаем текущую директорию проекта
$ProjectDir = Get-Location
$ProjectFile = "BrutalSandbox.csproj"
$OutputBaseDir = Join-Path $ProjectDir "build_releases"

Write-Host "====================================================" -ForegroundColor Cyan
Write-Host " НАСТРОЙКА ВЕРСИИ И НОМЕРА БИЛДА" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan

# Запрос номера билда у разработчика
$BuildVersion = Read-Host "Введите номер версии/билда (например, 1.0.2.45) [Нажмите Enter для 1.0.0.1]"
if ([string]::IsNullOrWhiteSpace($BuildVersion)) {
    $BuildVersion = "1.0.0.1"
}

# Очищаем или создаем папку для готовых файлов релизов
if (Test-Path $OutputBaseDir) { 
    Remove-Item $OutputBaseDir -Recurse -Force 
}
New-Item -ItemType Directory -Path $OutputBaseDir | Out-Null

Write-Host "`n====================================================" -ForegroundColor Cyan
Write-Host " ЗАПУСК СБОРКИ ПЛАТФОРМ ДЛЯ ВЕРСИИ: $BuildVersion" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan

# Аргументы компиляции с динамической подстановкой версии AssemblyVersion и FileVersion
$CommonArgs = "-c Release --self-contained true /p:PublishSingleFile=true /p:PublishTrimmed=true /p:TrimMode=partial /p:DebugType=none /p:DebugSymbols=false /p:Version=$BuildVersion /p:AssemblyVersion=$BuildVersion /p:FileVersion=$BuildVersion"

# -----------------------------------------------------------------
# 1. СБОРКА И УПАКОВКА ДЛЯ WINDOWS (win-x64)
# -----------------------------------------------------------------
Write-Host "`n[+] Компиляция и сборка для платформы: win-x64..." -ForegroundColor Green
$PublishDirWin = Join-Path $ProjectDir "bin\Release\net10.0\win-x64\publish"

Invoke-Expression "dotnet publish $ProjectFile -r win-x64 $CommonArgs"

if (Test-Path $PublishDirWin) {
    Write-Host "    [Упаковка]: Создание ZIP-архива для Windows..." -ForegroundColor Yellow
    $ZipPathWin = Join-Path $OutputBaseDir "BrutalSandbox_v$BuildVersion`_win-x64.zip"
    Compress-Archive -Path "$PublishDirWin\*" -DestinationPath $ZipPathWin -Force
    Write-Host "    [Успех]: Архив создан -> $ZipPathWin" -ForegroundColor Green
}

# -----------------------------------------------------------------
# 2. СБОРКА И УПАКОВКА ДЛЯ LINUX (linux-x64)
# -----------------------------------------------------------------
Write-Host "`n[+] Компиляция и сборка для платформы: linux-x64..." -ForegroundColor Green
$PublishDirLin = Join-Path $ProjectDir "bin\Release\net10.0\linux-x64\publish"

Invoke-Expression "dotnet publish $ProjectFile -r linux-x64 $CommonArgs"

if (Test-Path $PublishDirLin) {
    Write-Host "    [Упаковка]: Создание TAR.GZ архива для Linux..." -ForegroundColor Yellow
    $TarGzPath = Join-Path $OutputBaseDir "BrutalSandbox_v$BuildVersion`_linux-x64.tar.gz"
    
    Push-Location $PublishDirLin
    tar -czf $TarGzPath *
    Pop-Location
    
    Write-Host "    [Успех]: Архив создан -> $TarGzPath" -ForegroundColor Green
}

# -----------------------------------------------------------------
# 3. СБОРКА И УПАКОВКА ДЛЯ MACOS (osx-arm64)
# -----------------------------------------------------------------
Write-Host "`n[+] Компиляция и сборка для платформы: osx-arm64..." -ForegroundColor Green
$PublishDirMac = Join-Path $ProjectDir "bin\Release\net10.0\osx-arm64\publish"

Invoke-Expression "dotnet publish $ProjectFile -r osx-arm64 $CommonArgs"

if (Test-Path $PublishDirMac) {
    Write-Host "    [Упаковка]: Создание ZIP-архива для macOS..." -ForegroundColor Yellow
    $ZipPathMac = Join-Path $OutputBaseDir "BrutalSandbox_v$BuildVersion`_osx-arm64.zip"
    Compress-Archive -Path "$PublishDirMac\*" -DestinationPath $ZipPathMac -Force
    Write-Host "    [Успех]: Архив создан -> $ZipPathMac" -ForegroundColor Green
}

Write-Host "`n====================================================" -ForegroundColor Cyan
Write-Host " СБОРКА ЗАВЕРШЕНА! Созданы архивы версии $BuildVersion в: $OutputBaseDir" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan
