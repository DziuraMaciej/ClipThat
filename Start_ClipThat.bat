@echo off
setlocal EnableExtensions DisableDelayedExpansion
title ClipThat - Uruchamianie...
cd /d "%~dp0"

set "APP_DIR=%~dp0"
set "APP_EXE=%~dp0bin\Release\net8.0-windows10.0.22621.0\ClipThat.exe"
set "FORCE_REBUILD=0"
set "IS_MINIMIZED=0"
set "IS_EXIT_CMD=0"
set "APP_ARGS="

:ParseArgs
if "%~1"=="" goto :AfterArgs
if /I "%~1"=="--rebuild" (
    set "FORCE_REBUILD=1"
) else if /I "%~1"=="--exit" (
    set "IS_EXIT_CMD=1"
) else (
    if /I "%~1"=="--minimized" set "IS_MINIMIZED=1"
    set "APP_ARGS=%APP_ARGS% %1"
)
shift
goto :ParseArgs

:AfterArgs
if "%IS_EXIT_CMD%"=="1" (
    call :StopExistingInstance
    exit /b 0
)

rem Fast path when not forcing a rebuild and the compiled binary already exists:
rem 1) If ClipThat is already running, launch it directly so single-instance signaling wakes the window without wiping the replay buffer.
rem 2) If launching minimized (Windows autostart), start immediately.
rem 3) If no source files are newer than ClipThat.exe, skip dotnet build completely.
if "%FORCE_REBUILD%"=="0" if exist "%APP_EXE%" (
    tasklist /FI "IMAGENAME eq ClipThat.exe" 2>nul | find /I "ClipThat.exe" >nul
    if not errorlevel 1 (
        start "" "%APP_EXE%"%APP_ARGS%
        exit /b 0
    )

    if "%IS_MINIMIZED%"=="1" (
        start "" "%APP_EXE%"%APP_ARGS%
        exit /b 0
    )

    call :CheckNeedsRebuild
    if not errorlevel 1 (
        start "" "%APP_EXE%"%APP_ARGS%
        exit /b 0
    )
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
    "%DOTNET_EXE%" build -c Release --no-incremental --nologo -v m
) else (
    "%DOTNET_EXE%" build -c Release --nologo -v m
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

start "" "%APP_EXE%"%APP_ARGS%
exit /b 0

:CheckNeedsRebuild
rem Returns errorlevel 0 when binary is up to date, or errorlevel 1 when a rebuild is required.
powershell -NoProfile -Command "$exe = Get-Item -LiteralPath $env:APP_EXE -ErrorAction SilentlyContinue; if (-not $exe) { exit 1 }; $exeTime = $exe.LastWriteTimeUtc; $root = (Resolve-Path -LiteralPath $env:APP_DIR).Path; $newer = Get-ChildItem -LiteralPath $root -File -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\(bin|obj|ReleaseBuild|ClipThat\.Tests|\.git)\\' -and ($_.Extension -in @('.cs', '.xaml', '.csproj') -or $_.Name -eq 'app.ico') -and $_.LastWriteTimeUtc -gt $exeTime } | Select-Object -First 1; if ($newer) { exit 1 } else { exit 0 }" >nul 2>&1
exit /b %ERRORLEVEL%

:StopExistingInstance
tasklist /FI "IMAGENAME eq ClipThat.exe" 2>nul | find /I "ClipThat.exe" >nul
if errorlevel 1 exit /b 0

rem Prefer graceful single-instance shutdown via --exit (triggers ExitApplicationAsync and flushes resources cleanly).
if exist "%APP_EXE%" (
    "%APP_EXE%" --exit >nul 2>&1
)

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
if exist "%ProgramFiles%\dotnet\dotnet.exe" (
    "%ProgramFiles%\dotnet\dotnet.exe" --list-sdks 2>nul | findstr /R "^[89]\. ^[1-9][0-9]\." >nul 2>&1
    if not errorlevel 1 (
        set "PATH=%ProgramFiles%\dotnet;%PATH%"
        set "DOTNET_EXE=%ProgramFiles%\dotnet\dotnet.exe"
        exit /b 0
    )
)

where dotnet >nul 2>&1
if errorlevel 1 exit /b 0

dotnet --list-sdks 2>nul | findstr /R "^[89]\. ^[1-9][0-9]\." >nul 2>&1
if not errorlevel 1 (
    set "DOTNET_EXE=dotnet"
)
exit /b 0
