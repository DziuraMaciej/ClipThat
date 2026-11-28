@echo off
setlocal EnableExtensions DisableDelayedExpansion
title ClipThat - Aktualizacja...
cd /d "%~dp0"

set "REPO_ZIP_URL=https://github.com/DziuraMaciej/ClipThat/archive/refs/heads/main.zip"
set "APP_DIR=%~dp0"
set "APP_EXE=%~dp0bin\Release\net8.0-windows10.0.22621.0\ClipThat.exe"

(
    echo Zamykanie uruchomionej instancji ClipThat...
    tasklist /FI "IMAGENAME eq ClipThat.exe" 2>nul | find /I "ClipThat.exe" >nul
    if not errorlevel 1 (
        if exist "%APP_EXE%" (
            "%APP_EXE%" --exit >nul 2>&1
        )
        tasklist /FI "IMAGENAME eq ClipThat.exe" 2>nul | find /I "ClipThat.exe" >nul
        if not errorlevel 1 (
            taskkill /IM ClipThat.exe >nul 2>&1
            timeout /t 2 /nobreak >nul 2>&1
            tasklist /FI "IMAGENAME eq ClipThat.exe" 2>nul | find /I "ClipThat.exe" >nul
            if not errorlevel 1 (
                taskkill /F /IM ClipThat.exe >nul 2>&1
                timeout /t 1 /nobreak >nul 2>&1
            )
        )
    )

    if exist "%APP_DIR%.git" (
        where git >nul 2>&1
        if not errorlevel 1 (
            echo Wykryto repozytorium Git - sprawdzanie lokalnych zmian...
            for /f "delims=" %%S in ('git status --porcelain') do (
                echo.
                echo Wykryto niezapisane lokalne zmiany w repozytorium Git.
                echo Aktualizacja przerwana, aby nie nadpisac Twojej pracy.
                pause
                exit /b 1
            )
            echo Pobieranie aktualizacji przez git pull...
            git pull --ff-only
            if errorlevel 1 (
                echo.
                echo Nie udalo sie pobrac aktualizacji przez git pull.
                pause
                exit /b 1
            )
            echo Aktualizacja zakonczona. Uruchamianie ClipThat...
            call "%APP_DIR%Start_ClipThat.bat" --rebuild
            if errorlevel 1 exit /b 1
            exit /b 0
        )
    )

    echo Pobieranie najnowszej wersji ClipThat z GitHub...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; $appDir = (Resolve-Path -LiteralPath $env:APP_DIR).Path; $id = [Guid]::NewGuid().ToString('N'); $zipPath = Join-Path $env:TEMP ('ClipThat-update-' + $id + '.zip'); $extractDir = Join-Path $env:TEMP ('ClipThat-update-' + $id); $backupDir = Join-Path $env:TEMP ('ClipThat-backup-' + $id); $rootBackupDir = Join-Path $backupDir '_root'; $assetsBackupDir = Join-Path $backupDir '_assets'; $replacedDirs = @(); $backedUpRoot = $false; $backedUpAssets = $false; try { Invoke-WebRequest -Uri $env:REPO_ZIP_URL -OutFile $zipPath -UseBasicParsing; Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force; $srcRoot = Get-ChildItem -LiteralPath $extractDir -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'ClipThat.csproj') } | Select-Object -First 1; if (-not $srcRoot) { throw 'Pobrane archiwum nie zawiera pliku ClipThat.csproj.' }; New-Item -ItemType Directory -Force -Path $backupDir, $rootBackupDir, $assetsBackupDir | Out-Null; foreach ($dirName in @('Engine', 'Models', 'Services', 'UI', 'ClipThat.Tests')) { $srcSub = Join-Path $srcRoot.FullName $dirName; $dstSub = Join-Path $appDir $dirName; $bakSub = Join-Path $backupDir $dirName; if (Test-Path -LiteralPath $srcSub) { if (Test-Path -LiteralPath $dstSub) { Move-Item -LiteralPath $dstSub -Destination $bakSub -Force }; $replacedDirs += $dirName; Copy-Item -LiteralPath $srcSub -Destination $dstSub -Recurse -Force } }; $srcAssets = Join-Path $srcRoot.FullName 'Assets'; $dstAssets = Join-Path $appDir 'Assets'; if (Test-Path -LiteralPath $srcAssets) { if (Test-Path -LiteralPath $dstAssets) { Get-ChildItem -LiteralPath $dstAssets -File | Where-Object { $_.Name -ine 'ffmpeg.exe' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $assetsBackupDir $_.Name) -Force }; $backedUpAssets = $true }; New-Item -ItemType Directory -Force -Path $dstAssets | Out-Null; Get-ChildItem -LiteralPath $srcAssets -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $dstAssets $_.Name) -Force } }; Get-ChildItem -LiteralPath $appDir -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $rootBackupDir $_.Name) -Force }; $backedUpRoot = $true; Get-ChildItem -LiteralPath $srcRoot.FullName -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $appDir $_.Name) -Force }; Get-ChildItem -LiteralPath $appDir -File -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\(bin|obj|ReleaseBuild)\\' } | Unblock-File -ErrorAction SilentlyContinue } catch { foreach ($dirName in $replacedDirs) { $dstSub = Join-Path $appDir $dirName; $bakSub = Join-Path $backupDir $dirName; if (Test-Path -LiteralPath $bakSub) { if (Test-Path -LiteralPath $dstSub) { Remove-Item -LiteralPath $dstSub -Recurse -Force -ErrorAction SilentlyContinue }; Move-Item -LiteralPath $bakSub -Destination $dstSub -Force -ErrorAction SilentlyContinue } }; if ($backedUpAssets) { $dstAssets = Join-Path $appDir 'Assets'; Get-ChildItem -LiteralPath $assetsBackupDir -File -ErrorAction SilentlyContinue | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $dstAssets $_.Name) -Force -ErrorAction SilentlyContinue } }; if ($backedUpRoot) { Get-ChildItem -LiteralPath $rootBackupDir -File -ErrorAction SilentlyContinue | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $appDir $_.Name) -Force -ErrorAction SilentlyContinue } }; throw } finally { Remove-Item -LiteralPath $zipPath, $extractDir, $backupDir -Recurse -Force -ErrorAction SilentlyContinue }"
    if errorlevel 1 (
        echo.
        echo Blad podczas pobierania lub rozpakowywania aktualizacji z GitHub.
        echo Sprawdz polaczenie z internetem oraz upewnij sie, ze repozytorium jest publiczne
        echo (dla prywatnego repozytorium uzyj jednorazowo git clone zamiast Download ZIP).
        pause
        exit /b 1
    )

    echo Aktualizacja plikow zakonczona. Budowanie i uruchamianie ClipThat...
    call "%APP_DIR%Start_ClipThat.bat" --rebuild
    if errorlevel 1 exit /b 1
    exit /b 0
)
