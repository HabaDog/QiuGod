@echo off
setlocal EnableExtensions DisableDelayedExpansion

for %%I in ("%~dp0.") do set "PROJECT_ROOT=%%~fI"
cd /d "%PROJECT_ROOT%"
title MyDemo Server

echo ========================================
echo   MyDemo Server
echo ========================================
echo.

where powershell.exe >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Windows PowerShell was not found.
    goto :failed
)

where docker >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Docker CLI was not found.
    echo Start Docker Desktop, then run this file again.
    goto :failed
)

where dotnet.exe >nul 2>&1
if errorlevel 1 (
    echo [ERROR] The .NET SDK was not found.
    goto :failed
)

if not exist "%PROJECT_ROOT%\.env" (
    echo [ERROR] Missing configuration file: %PROJECT_ROOT%\.env
    goto :failed
)

if not exist "%PROJECT_ROOT%\docker-compose.yml" (
    echo [ERROR] Missing Docker Compose file: %PROJECT_ROOT%\docker-compose.yml
    goto :failed
)

if not exist "%PROJECT_ROOT%\Server\Start-All.ps1" (
    echo [ERROR] Missing multi-service startup script: %PROJECT_ROOT%\Server\Start-All.ps1
    goto :failed
)

echo [INFO] Starting Match, Lobby, GameServer, and Gateway services...
echo [INFO] Extra arguments are forwarded to Start-All.ps1.
echo [INFO] Example: Start-Server.bat -SkipRestore
echo [INFO] LAN example: Start-Server.bat -Lan
echo.

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%PROJECT_ROOT%\Server\Start-All.ps1" %*
set "SERVER_EXIT_CODE=%ERRORLEVEL%"

echo.
if not "%SERVER_EXIT_CODE%"=="0" (
    echo [ERROR] Server startup script stopped with exit code %SERVER_EXIT_CODE%.
) else (
    echo [INFO] Server startup script has stopped.
)
goto :end

:failed
set "SERVER_EXIT_CODE=1"
echo.

:end
pause
exit /b %SERVER_EXIT_CODE%
