@echo off
setlocal EnableExtensions
title ClipThat - Uruchamianie...
cd /d "%~dp0"

taskkill /F /IM ClipThat.exe >nul 2>&1
timeout /t 1 /nobreak >nul 2>&1

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
"%DOTNET_EXE%" build-server shutdown >nul 2>&1
"%DOTNET_EXE%" build -c Release -m:1 --nologo -v q
if errorlevel 1 (
    echo.
    echo Blad budowania aplikacji ClipThat.
    pause
    exit /b 1
)

if not exist "bin\Release\net8.0-windows10.0.22621.0\ClipThat.exe" (
    echo.
    echo Nie znaleziono skompilowanego pliku ClipThat.exe.
    pause
    exit /b 1
)

start "" "%~dp0bin\Release\net8.0-windows10.0.22621.0\ClipThat.exe"
exit

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
