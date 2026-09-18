; =====================================================================
;  Inno Setup — Instalador do OpenTEC-Hub.
;  A versão é lida automaticamente do binário publicado (ver MyAppVersion abaixo).
;  Empacota a publicação self-contained win-x64 (.NET 10 embutido — não
;  exige .NET pré-instalado no computador de bancada de destino).
;
;  Como compilar:
;    1. Gere a publicação self-contained:
;         dotnet publish ..\src\OpenTECHub\OpenTECHub.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
;    2. Instale o Inno Setup 6 (https://jrsoftware.org/isdl.php).
;    3. Compile este script:
;         "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" OpenTECHub_Setup.iss
;       (ou utilize o script auxiliar build_installer.ps1)
;    4. O instalador final sai em installer/Output/.
;
;  Para apontar para outra pasta de publicação, passe /DPublishDir:
;    ISCC.exe /DPublishDir="C:\caminho\publish" OpenTECHub_Setup.iss
; =====================================================================

#define MyAppName "OpenTEC-Hub"
#define MyAppPublisher "Vitor Mazziero UNESP"
#define MyAppExeName "OpenTECHub.exe"

; Pasta da publicação self-contained (relativa a este .iss). Sobrescrevível com /DPublishDir.
#ifndef PublishDir
  #define PublishDir "..\src\OpenTECHub\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"
#endif

; Versão lida AUTOMATICAMENTE do binário publicado (fonte única: <Version> em
; Directory.Build.props). Ao mudar a versão lá e republicar, o instalador acompanha
; sozinho — não há número para editar manualmente aqui. Lê o campo ProductVersion (ex.: "0.24.0")
; do recurso de versão da OpenTECHub.dll.
#define MyAppDll PublishDir + "\OpenTECHub.dll"
#if !FileExists(MyAppDll)
  #error Publicacao nao encontrada. Rode 'dotnet publish ..\src\OpenTECHub\OpenTECHub.csproj -c Release -r win-x64 --self-contained true' antes de compilar o instalador (ou passe /DPublishDir).
#endif
; O MinVer grava em ProductVersion a InformationalVersion completa, que inclui o
; metadado de build apos '+' (ex.: "0.26.4+c479d32..."). Esse sufixo e o SHA do
; commit: nao distingue versoes e polui tanto o nome do instalador quanto o que
; aparece em Programas e Recursos. Fica so a versao semantica; num build sem tag
; o pre-release e preservado ("0.26.4-dev.8"), que continua informativo.
#define MyAppVersionRaw GetStringFileInfo(MyAppDll, PRODUCT_VERSION)
#if Pos("+", MyAppVersionRaw) > 0
  #define MyAppVersion Copy(MyAppVersionRaw, 1, Pos("+", MyAppVersionRaw) - 1)
#else
  #define MyAppVersion MyAppVersionRaw
#endif

[Setup]
AppId={{E1D4978F-5F1A-4C2E-A37F-02D437F9E821}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\OpenTECHub\Resources\Icons\app_icon.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=OpenTECHub_Setup_v{#MyAppVersion}

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Copia toda a pasta de publicação (executável, DLLs do .NET embutido, runtime SkiaSharp,
; arquivos de tema e dependências). recursesubdirs garante a cópia completa das pastas.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Desinstalar {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
