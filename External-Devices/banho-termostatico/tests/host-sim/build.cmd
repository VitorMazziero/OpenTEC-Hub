@echo off
rem Compila e roda a simulacao no PC com o MSVC (Visual Studio 2022+ Build Tools ou Community).
rem Uso: build.cmd [-v]   (-v mostra o log serial do firmware)
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
set "SRC=%~dp0..\..\firmware\thermostatic-bath\src"
if not exist build mkdir build
cl /nologo /EHsc /std:c++17 /W3 /utf-8 /I stubs /I "%SRC%" /Fo:build\ sim.cpp "%SRC%\setpoint\SetpointManager.cpp" "%SRC%\setpoint\SetpointGuard.cpp" "%SRC%\keypad\KeyPresser.cpp" "%SRC%\keypad\KeySense.cpp" "%SRC%\protocol\ConfigCodec.cpp" "%SRC%\core\AppContext.cpp" /Fe:build\sim.exe
if errorlevel 1 exit /b 1
build\sim.exe %*
