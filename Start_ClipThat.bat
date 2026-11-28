@echo off
setlocal EnableExtensions EnableDelayedExpansion
title ClipThat - Uruchamianie...
cd /d "%~dp0"

set "APP_EXE=%~dp0bin\Release\net8.0-windows10.0.22621.0\ClipThat.exe"
set "FORCE_REBUILD=0"
set "IS_MINIMIZED=0"
set "APP_ARGS="

for %%A in (%*) do (
    if /I "%%~A"=="--rebuild" (
        set "FORCE_REBUILD=1"
    ) else (
        if /I "%%~A"=="--minimized" set "IS_MINIMIZED=1"
        set "APP_ARGS=!APP_ARGS! %%A"
    )
)

rem Fast autostart path: when launched with --minimized and the binary already exists, start immediately without rebuilding.
if "%IS_MINIMIZED%"=="1" if "%FORCE_REBUILD%"=="0" if exist "%APP_EXE%" (
    start "" "%APP_EXE%"!APP_ARGS!
    exit /b 0
)

call :StopExistingInstance

call :ResolveDotnet
if not defined DOTNET_EXE (
    echo Instalowanie srodowiska Microsoft .NET 8 SDK...
    winget install Microsoft.DotNet.SDK.8 --accept-package-agreements --accept-source-agreements
    if errorlevel 1 (
        echo.
        echo Nie udalo sie automatycznie zainstalowac .NET 8 SDK.
        echo Pobierz recznie .NET 8 SDK ze strony Microsoftu i uruchom ponownie Start_ClipThat.bat:
        echo https://dotnet.microsoft.com/download/dotnet/8.0
        pause
        exit /b 1
    )

    if exist "%ProgramFiles%\dotnet\dotnet.exe" (
        set "PATH=%ProgramFiles%\dotnet;%PATH%"
    )
    call :ResolveDotnet
    if not defined DOTNET_EXE (
        echo.
        echo Instalacja .NET 8 SDK zakonczona. Uruchom ponownie Start_ClipThat.bat.
        pause
        exit /b 0
    )
)

echo Budowanie aplikacji ClipThat (przy pierwszym uruchomieniu pobierany jest FFmpeg)...
if "%FORCE_REBUILD%"=="1" (
    "%DOTNET_EXE%" build -c Release --no-incremental --nologo -v q
) else (
    "%DOTNET_EXE%" build -c Release --nologo -v q
)
if errorlevel 1 (
    echo.
    echo Blad budowania aplikacji ClipThat.
    pause
    exit /b 1
)

if not exist "%APP_EXE%" (
    echo.
    echo Nie znaleziono skompilowanego pliku ClipThat.exe.
    pause
    exit /b 1
)

start "" "%APP_EXE%"!APP_ARGS!
exit /b 0

:StopExistingInstance
tasklist /FI "IMAGENAME eq ClipThat.exe" 2>nul | find /I "ClipThat.exe" >nul
if errorlevel 1 exit /b 0
taskkill /IM ClipThat.exe >nul 2>&1
timeout /t 2 /nobreak >nul 2>&1
tasklist /FI "IMAGENAME eq ClipThat.exe" 2>nul | find /I "ClipThat.exe" >nul
if not errorlevel 1 (
    taskkill /F /IM ClipThat.exe >nul 2>&1
    timeout /t 1 /nobreak >nul 2>&1
)
exit /b 0

:ResolveDotnet
set "DOTNET_EXE="
set "CANDIDATE="
where dotnet >nul 2>&1
if not errorlevel 1 (
    set "CANDIDATE=dotnet"
) else if exist "%ProgramFiles%\dotnet\dotnet.exe" (
    set "PATH=%ProgramFiles%\dotnet;%PATH%"
    set "CANDIDATE=%ProgramFiles%\dotnet\dotnet.exe"
)
if not defined CANDIDATE exit /b 0

"%CANDIDATE%" --list-sdks 2>nul | findstr /R "^[89]\. ^[1-9][0-9]\." >nul 2>&1
if not errorlevel 1 (
    set "DOTNET_EXE=%CANDIDATE%"
)
exit /b 0
