@echo off
setlocal EnableExtensions
title ClipThat - Aktualizacja...
cd /d "%~dp0"

set "REPO_ZIP_URL=https://github.com/DziuraMaciej/ClipThat/archive/refs/heads/main.zip"
set "APP_DIR=%~dp0"

(
    echo Zamykanie uruchomionej instancji ClipThat...
    taskkill /F /IM ClipThat.exe >nul 2>&1
    timeout /t 1 /nobreak >nul 2>&1

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
            call "%APP_DIR%Start_ClipThat.bat"
            exit /b %errorlevel%
        )
    )

    echo Pobieranie najnowszej wersji ClipThat z GitHub...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; $appDir = (Resolve-Path -LiteralPath $env:APP_DIR).Path; $id = [Guid]::NewGuid().ToString('N'); $zipPath = Join-Path $env:TEMP ('ClipThat-update-' + $id + '.zip'); $extractDir = Join-Path $env:TEMP ('ClipThat-update-' + $id); try { Invoke-WebRequest -Uri $env:REPO_ZIP_URL -OutFile $zipPath -UseBasicParsing; Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force; $srcRoot = Get-ChildItem -Path $extractDir -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'ClipThat.csproj') } | Select-Object -First 1; if (-not $srcRoot) { throw 'Pobrane archiwum nie zawiera pliku ClipThat.csproj.' }; foreach ($dirName in @('Engine', 'Models', 'Services', 'UI')) { $srcSub = Join-Path $srcRoot.FullName $dirName; $dstSub = Join-Path $appDir $dirName; if (Test-Path $srcSub) { if (Test-Path $dstSub) { Remove-Item -Path $dstSub -Recurse -Force }; Copy-Item -Path $srcSub -Destination $dstSub -Recurse -Force } }; $srcAssets = Join-Path $srcRoot.FullName 'Assets'; $dstAssets = Join-Path $appDir 'Assets'; if (Test-Path $srcAssets) { New-Item -ItemType Directory -Force -Path $dstAssets | Out-Null; Get-ChildItem -Path $srcAssets -File | ForEach-Object { Copy-Item -Path $_.FullName -Destination (Join-Path $dstAssets $_.Name) -Force } }; Get-ChildItem -Path $srcRoot.FullName -File | ForEach-Object { Copy-Item -Path $_.FullName -Destination (Join-Path $appDir $_.Name) -Force }; Get-ChildItem -Path $appDir -File -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\(bin|obj|ReleaseBuild)\\' } | Unblock-File -ErrorAction SilentlyContinue } finally { Remove-Item -Path $zipPath, $extractDir -Recurse -Force -ErrorAction SilentlyContinue }"
    if errorlevel 1 (
        echo.
        echo Blad podczas pobierania lub rozpakowywania aktualizacji z GitHub.
        echo Sprawdz polaczenie z internetem oraz upewnij sie, ze repozytorium jest publiczne
        echo (dla prywatnego repozytorium uzyj jednorazowo git clone zamiast Download ZIP).
        pause
        exit /b 1
    )

    echo Aktualizacja plikow zakonczona. Budowanie i uruchamianie ClipThat...
    call "%APP_DIR%Start_ClipThat.bat"
    exit /b %errorlevel%
)
