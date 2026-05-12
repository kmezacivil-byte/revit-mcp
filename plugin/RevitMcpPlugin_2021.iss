; ============================================================
;  Inno Setup Script — RevitMCP Plugin para Revit 2021
;  Digital Jump Peru
;  Generado para Inno Setup 6.x
; ============================================================

#define MyAppName      "RevitMCP Plugin"
#define MyAppVersion   "1.0.0"
#define MyAppPublisher "Digital Jump Peru"
#define MyRevitYear    "2021"
#define MyAddInId      "F1E2D3C4-B5A6-7890-ABCD-EF1234567890"
#define MyAddInFile    "RevitMcpPlugin.addin"
#define MyDllFile      "RevitMcpPlugin.dll"

[Setup]
AppId                    ={{F1E2D3C4-B5A6-7890-ABCD-EF1234567890}
AppName                  ={#MyAppName}
AppVersion               ={#MyAppVersion}
AppVerName               ={#MyAppName} {#MyAppVersion} (Revit {#MyRevitYear})
AppPublisher             ={#MyAppPublisher}
AppPublisherURL          =https://digitaljumpperu.com
DefaultDirName           ={commonpf64}\{#MyAppPublisher}\RevitMcpPlugin
DefaultGroupName         ={#MyAppName}
OutputDir                =..\installer
OutputBaseFilename       =RevitMcpPlugin_{#MyRevitYear}_Setup
Compression              =lzma2/ultra64
SolidCompression         =yes
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion               =6.1sp1
PrivilegesRequired       =admin
UninstallDisplayName     ={#MyAppName} (Revit {#MyRevitYear})
UninstallDisplayIcon     ={app}\{#MyDllFile}
DisableProgramGroupPage  =yes
WizardStyle              =modern

; Ícono del instalador (opcional — comentar si no existe el archivo)
; SetupIconFile=assets\icon.ico

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

; ─────────────────────────────────────────────
;  Textos personalizados en español
; ─────────────────────────────────────────────
[CustomMessages]
spanish.RevitNotFound=Revit {#MyRevitYear} no fue encontrado en este equipo.%nEl plugin puede instalarse igual, pero no funcionará hasta que instales Revit {#MyRevitYear}.%n%n¿Deseas continuar de todas formas?
spanish.AddinCreated=Archivo .addin creado correctamente en:%n%1
spanish.AddinFailed=No se pudo crear el archivo .addin en:%n%1%n%nVerifica los permisos de la carpeta.

; ─────────────────────────────────────────────
;  Archivos a instalar
; ─────────────────────────────────────────────
[Files]
; Solo el DLL del plugin — Newtonsoft.Json se toma del que ya viene con Revit 2021
Source: "build-output-2021\{#MyDllFile}"; DestDir: "{app}"; Flags: ignoreversion

; ─────────────────────────────────────────────
;  Accesos directos (Inicio)
; ─────────────────────────────────────────────
[Icons]
Name: "{group}\Desinstalar {#MyAppName}"; Filename: "{uninstallexe}"

; ─────────────────────────────────────────────
;  Limpieza al desinstalar
; ─────────────────────────────────────────────
[UninstallDelete]
; El .addin se borra manualmente desde el Code (ver DeinitializeUninstall)
Type: filesandordirs; Name: "{app}"

; ─────────────────────────────────────────────
;  Código Pascal — lógica de instalación
; ─────────────────────────────────────────────
[Code]

// ── Constantes ─────────────────────────────────────────────────────────────
const
  RevitAddinsDir = '{commonappdata}\Autodesk\Revit\Addins\{#MyRevitYear}\';
  AddinFileName  = '{#MyAddInFile}';

// ── Comprueba si Revit 2021 está instalado ──────────────────────────────────
function IsRevitInstalled(): Boolean;
var
  RevitPath: string;
begin
  RevitPath := ExpandConstant('{commonpf64}\Autodesk\Revit {#MyRevitYear}\RevitAPI.dll');
  Result := FileExists(RevitPath);
  // Fallback: intentar x86
  if not Result then
  begin
    RevitPath := ExpandConstant('{commonpf32}\Autodesk\Revit {#MyRevitYear}\RevitAPI.dll');
    Result := FileExists(RevitPath);
  end;
end;

// ── Genera y guarda el archivo .addin ───────────────────────────────────────
function WriteAddinFile(DllPath: string): Boolean;
var
  AddinDir, AddinPath, Content: string;
begin
  AddinDir  := ExpandConstant(RevitAddinsDir);
  AddinPath := AddinDir + AddinFileName;

  // Crear directorio si no existe
  ForceDirectories(AddinDir);

  Content :=
    '<?xml version="1.0" encoding="utf-8"?>' + #13#10 +
    '<RevitAddIns>' + #13#10 +
    '  <AddIn Type="Application">' + #13#10 +
    '    <Name>RevitMcpPlugin</Name>' + #13#10 +
    '    <Assembly>' + DllPath + '</Assembly>' + #13#10 +
    '    <AddInId>{#MyAddInId}</AddInId>' + #13#10 +
    '    <FullClassName>RevitMcpPlugin.Plugin</FullClassName>' + #13#10 +
    '    <VendorId>DJUMP</VendorId>' + #13#10 +
    '    <VendorDescription>Digital Jump Peru</VendorDescription>' + #13#10 +
    '  </AddIn>' + #13#10 +
    '</RevitAddIns>';

  Result := SaveStringToFile(AddinPath, Content, False);

  if Result then
    MsgBox(FmtMessage(CustomMessage('AddinCreated'), [AddinPath]),
           mbInformation, MB_OK)
  else
    MsgBox(FmtMessage(CustomMessage('AddinFailed'), [AddinPath]),
           mbError, MB_OK);
end;

// ── Elimina el .addin al desinstalar ────────────────────────────────────────
procedure DeleteAddinFile();
var
  AddinPath: string;
begin
  AddinPath := ExpandConstant(RevitAddinsDir) + AddinFileName;
  if FileExists(AddinPath) then
    DeleteFile(AddinPath);
end;

// ── Verificación antes de instalar ──────────────────────────────────────────
function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsRevitInstalled() then
  begin
    if MsgBox(FmtMessage(CustomMessage('RevitNotFound'), ['']),
              mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
end;

// ── Post-instalación: escribir el .addin ────────────────────────────────────
procedure CurStepChanged(CurStep: TSetupStep);
var
  DllPath: string;
begin
  if CurStep = ssPostInstall then
  begin
    DllPath := ExpandConstant('{app}\{#MyDllFile}');
    WriteAddinFile(DllPath);
  end;
end;

// ── Pre-desinstalación: eliminar el .addin ──────────────────────────────────
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    DeleteAddinFile();
end;
