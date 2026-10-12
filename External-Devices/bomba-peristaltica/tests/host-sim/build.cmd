@echo off
rem Compila e roda a simulacao no PC da bomba com o MSVC (Visual Studio com C++).
rem So os cabecalhos sem Arduino: protocol/JsonScan.h, control/DoseMath.h, control/FrameValidate.h.
setlocal
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
  echo vswhere.exe nao encontrado; instale o Visual Studio com "Desktop development with C++".
  exit /b 1
)
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if not defined VSDIR (
  echo Nenhum Visual Studio com o compilador C++ encontrado.
  exit /b 1
)
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul
cd /d "%~dp0"
set "SRC=%~dp0..\..\firmware\peristaltic-pump\src"
if not exist build mkdir build
cl /nologo /EHsc /std:c++17 /W4 /utf-8 /I "%SRC%" /Fo:build\ sim.cpp /Fe:build\sim.exe
if errorlevel 1 exit /b 1
build\sim.exe
