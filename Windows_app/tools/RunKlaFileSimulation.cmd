@echo off
setlocal
set "ROOT=%~dp0.."
set "APP=%ROOT%\src\OpenTECHub\bin\Debug\net10.0-windows10.0.19041.0\win-x64\OpenTECHub.exe"
set "DEFAULT_DATA=D:\OneDrive\Doutorado_CNPq\_Artigos_e_Coorientacoes\Artigos\06_kLa_Modelo\Dados\Testes bioticos e abioticos\TRANSF O2 P1 2026_0,5 VVM.txt"

dotnet build "%ROOT%\OpenTECHub.slnx" --no-restore --verbosity minimal
if errorlevel 1 exit /b 1

if "%~1"=="" (
  set "DATA=%DEFAULT_DATA%"
) else (
  set "DATA=%~1"
)

if "%~2"=="" (
  set "SPEED=10"
) else (
  set "SPEED=%~2"
)

if not exist "%DATA%" (
  echo Arquivo de simulacao nao encontrado: "%DATA%"
  exit /b 2
)

start "OpenTEC-Hub - Simulacao kLa" "%APP%" --kla-test-file "%DATA%" --kla-test-speed "%SPEED%"
