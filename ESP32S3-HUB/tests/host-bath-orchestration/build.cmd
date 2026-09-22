@echo off
setlocal
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" exit /b 1
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if not defined VSDIR exit /b 1
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul
cd /d "%~dp0"
if not exist build mkdir build
cl /nologo /EHsc /std:c++17 /W4 /utf-8 /Fo:build\ orchestration_test.cpp ..\..\ESP32S3-HUB\src\control\BathCommandCoordinator.cpp /Fe:build\orchestration_test.exe
if errorlevel 1 exit /b 1
build\orchestration_test.exe
