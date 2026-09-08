; Inno Setup script for JussiMiniPos.
;
; Build it with installer\build.ps1, which publishes the application first and
; then compiles this. Compiling it on its own works too, but only after a
; self-contained publish exists at the path below — the version is read out of
; that executable so the installer and the application can never disagree
; about what they are.
;
;   Requires Inno Setup 6.3 or newer (for ArchitecturesAllowed=x64compatible).
;   https://jrsoftware.org/isdl.php

#define AppName "JussiMiniPos"
#define AppPublisher "Jussi Alanen"
#define AppUrl "https://github.com/jussipalanen/jussi-mini-pos"
#define AppExeName "JussiMiniPos.exe"

; Where "dotnet publish -c Release -r win-x64 --self-contained true" leaves it.
#define PublishDir "..\bin\Release\net10.0-windows\win-x64\publish"
#define AppExe PublishDir + "\" + AppExeName

#if !FileExists(AppExe)
  #error Publish the application first: run installer\build.ps1, or dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
#endif

; .NET writes <Version> into ProductVersion and appends "+<commit>" to it when
; the build knows its source revision. Everything from the plus sign on is a
; build detail rather than a version, and Services/AppInfo.cs cuts it the same
; way, so the number in the installer matches the one on the start screen.
#define RawVersion GetStringFileInfo(AppExe, "ProductVersion")
#define PlusAt Pos("+", RawVersion)
#if PlusAt > 0
  #define AppVersion Copy(RawVersion, 1, PlusAt - 1)
#else
  #define AppVersion RawVersion
#endif

[Setup]
; Never change AppId: it is how Windows recognises an existing install to
; upgrade, and how the uninstaller finds itself. A new one would leave the old
; entry stranded in Add/Remove Programs.
AppId={{7B3E5A94-2C61-4F8D-9A5E-1D0F6C8B4A27}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} {#AppVersion}

; Per-machine install under Program Files, so every Windows account on a till
; runs the same build. Each account still gets its own database — see the note
; in the README about %LOCALAPPDATA%.
PrivilegesRequired=admin

; The published payload is win-x64. x64compatible rather than x64 so it also
; installs on ARM64 Windows, which runs x64 under emulation.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; .NET 10 needs Windows 10 or newer.
MinVersion=10.0

OutputDir=Output
OutputBaseFilename={#AppName}-{#AppVersion}-setup
SetupIconFile=..\Assets\Icons\icon\favicon.ico

; The payload is a self-contained single file of about 136 MB, nearly all of
; it the .NET runtime, which compresses well. Solid compression helps because
; it is one big file rather than many small ones.
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; Numeric, because the Win32 version resource cannot hold "-beta.2".
VersionInfoVersion={#GetVersionNumbersString(AppExe)}
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

[Languages]
; The application's own text is Finnish, so the installer offers Finnish and
; falls back to English. Delete the Finnish line if your Inno Setup install
; does not ship Finnish.isl.
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "finnish"; MessagesFile: "compiler:Languages\Finnish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Only the executable. The publish folder also holds a .pdb, which is debug
; symbols and has no business on a till.
Source: "{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Nothing is listed under [UninstallDelete]: the application's data lives in
; %LOCALAPPDATA%\JussiMiniPos and holds the sales history and the product
; images. Removing it silently would destroy a till's records, so it is only
; ever removed after the question asked in [Code] below.

[Code]
const
  DataFolderName = 'JussiMiniPos';

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  // Resolved in the context of whoever is uninstalling, which for a
  // per-machine install is an administrator. So this offers to remove that
  // account's data and cannot reach another user's — deliberate, since one
  // person uninstalling must not wipe a colleague's sales history.
  DataDir := ExpandConstant('{localappdata}\' + DataFolderName);

  if not DirExists(DataDir) then
    Exit;

  // Defaults to No: an absent-minded Enter keeps the data.
  if MsgBox(
       'Poistetaanko myös myyntitiedot, tuotteet ja tuotekuvat?' + #13#10#13#10 +
       DataDir + #13#10#13#10 +
       'Valitse Ei, jos haluat säilyttää tietokannan.',
       mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(DataDir, True, True, True);
end;
