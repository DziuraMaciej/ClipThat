@echo off
title ClipThat - Uruchamianie...
cd /d "%~dp0"

taskkill /F /IM ClipThat.exe >nul 2>&1

where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo Instalowanie srodowiska Microsoft .NET 8 SDK...
    winget install Microsoft.DotNet.SDK.8 --accept-package-agreements --accept-source-agreements
    if %errorlevel% neq 0 (
        echo Pobierz recznie .NET 8 SDK ze strony Microsoftu i uruchom ponownie Start_ClipThat.bat:
        echo https://dotnet.microsoft.com/download/dotnet/8.0
        pause
        exit /b 1
    )
    echo Instalacja zakonczona. Uruchom ponownie Start_ClipThat.bat.
    pause
    exit /b 0
)

dotnet build-server shutdown >nul 2>&1
dotnet build -c Release -m:1 --nologo -v q
if %errorlevel% neq 0 (
    echo Blad budowania aplikacji ClipThat.
    pause
    exit /b 1
)

start "" "bin\Release\net8.0-windows10.0.22621.0\ClipThat.exe"
exit