@echo off
setlocal EnableExtensions DisableDelayedExpansion

for %%I in ("%~dp0.") do set "PROJECT_ROOT=%%~fI"
cd /d "%PROJECT_ROOT%"
title MyDemo LAN Client Build and Package

set "CLIENT_PROJECT=%PROJECT_ROOT%\Client"
set "UNITY_EXE=C:\Program Files\Unity\Hub\Editor\2022.3.62f3\Editor\Unity.exe"
set "BUILD_LOG_DIR=%PROJECT_ROOT%\BuildLogs"
set "BUILD_LOG=%BUILD_LOG_DIR%\WindowsClientBuild.log"

echo ========================================
echo   MyDemo LAN Client Build and Package
echo ========================================
echo.

if not exist "%UNITY_EXE%" (
    echo [ERROR] Unity 2022.3.62f3 was not found:
    echo         %UNITY_EXE%
    echo Update UNITY_EXE in this BAT if Unity was installed elsewhere.
    goto :failed
)

rem Unity leaves Temp\UnityLockfile behind after a crash or forced shutdown.
rem Test the lock itself instead of assuming every existing file is active.
if exist "%CLIENT_PROJECT%\Temp\UnityLockfile" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$path='%CLIENT_PROJECT%\Temp\UnityLockfile'; try { $handle=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); $handle.Close(); exit 0 } catch { exit 1 }"
    if errorlevel 1 (
        echo [ERROR] A Unity Editor process is currently using this project.
        echo Close Unity first, then run this BAT again.
        goto :failed
    )
    echo [INFO] Removing stale UnityLockfile left by a previous Unity process...
    del /f /q "%CLIENT_PROJECT%\Temp\UnityLockfile" >nul 2>&1
    if exist "%CLIENT_PROJECT%\Temp\UnityLockfile" (
        echo [ERROR] Could not remove the stale UnityLockfile. Check file permissions.
        goto :failed
    )
)

if not exist "%PROJECT_ROOT%\Prepare-LanClient.ps1" (
    echo [ERROR] Prepare-LanClient.ps1 was not found.
    goto :failed
)

if not exist "%BUILD_LOG_DIR%" mkdir "%BUILD_LOG_DIR%"

echo [INFO] Building the Windows client with all code compiled into the player...
"%UNITY_EXE%" -batchmode -nographics -quit -projectPath "%CLIENT_PROJECT%" -executeMethod CommandLineClientBuild.BuildWindowsClient -logFile "%BUILD_LOG%"
if errorlevel 1 (
    echo.
    echo [ERROR] Unity build failed. See:
    echo         %BUILD_LOG%
    goto :failed
)

if not exist "%PROJECT_ROOT%\ClientBuild\MyClient.exe" (
    echo [ERROR] Unity reported success but MyClient.exe was not produced.
    goto :failed
)

echo [OK] Unity build succeeded. Creating the LAN delivery package...
if "%~1"=="" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PROJECT_ROOT%\Prepare-LanClient.ps1"
) else (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PROJECT_ROOT%\Prepare-LanClient.ps1" -ServerHost "%~1"
)

if errorlevel 1 (
    echo.
    echo [ERROR] Client package generation failed.
    goto :failed
)

echo.
echo [OK] The ZIP file is in: %PROJECT_ROOT%\Delivery
echo [OK] Send the newest ZIP to LAN players. They extract it and run MyClient.exe.
goto :end

:failed
echo.

:end
pause
exit /b
