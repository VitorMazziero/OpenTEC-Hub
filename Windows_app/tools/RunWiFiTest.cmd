@echo off
REM ===========================================================================
REM  OpenTEC-Hub - Phase 0 Wi-Fi validation
REM
REM  Runs unattended. You will have no internet while joined to the device's
REM  access point, so this needs nothing from the network except the ESP32.
REM
REM  BEFORE RUNNING:
REM    1. Connect Windows Wi-Fi to  Modulo_OpenTEC_1
REM    2. Windows will warn "no internet" - that is expected, stay connected
REM    3. Double-click this file
REM
REM  Takes about 3 minutes. Results are written to:
REM    %TEMP%\opentec-wifi-test\<timestamp>\
REM ===========================================================================

setlocal
cd /d "%~dp0"

echo.
echo  OpenTEC-Hub - Wi-Fi validation
echo  =============================
echo.
echo  Target device : 192.168.4.1  (Modulo_OpenTEC_1)
echo  Results folder: %TEMP%\opentec-wifi-test\
echo.
echo  This takes about 3 minutes. Do not disconnect the Wi-Fi while it runs.
echo.
pause

REM Prefer the pre-published self-contained build: it needs no SDK and no
REM restore, so nothing can go looking for nuget.org while offline.
set "PUBLISHED=%~dp0wifi-test\opentec-harness.exe"

if exist "%PUBLISHED%" (
    echo  Using published build.
    echo.
    "%PUBLISHED%" wifi-test 192.168.4.1
) else (
    echo  Published build not found, falling back to 'dotnet run'.
    echo.
    pushd "%~dp0.."
    dotnet run --project src\OpenTECHub.Harness --no-build -- wifi-test 192.168.4.1
    popd
)

set "EXITCODE=%ERRORLEVEL%"

echo.
echo  ==========================================================
if "%EXITCODE%"=="0" (
    echo   RESULT: all checks passed.
) else (
    echo   RESULT: there were failures - exit code %EXITCODE%
    echo   The report still contains everything that was measured.
)
echo  ==========================================================
echo.
echo  Opening the results folder...
start "" "%TEMP%\opentec-wifi-test"
echo.
echo  Reconnect your normal Wi-Fi, then tell Claude the test is done.
echo.
pause
endlocal
