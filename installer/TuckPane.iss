#define MyAppName "TuckPane"
#ifndef MyAppVersion
  #define MyAppVersion "4.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif
#ifndef WebView2Installer
  #define WebView2Installer "..\artifacts\dependencies\webview2\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
#endif

[Setup]
#ifdef UpdateValidation
AppId={{B9B32869-525B-4C06-9B77-7680D4929F9D}
#else
AppId={{2B7D4C50-0148-4D5C-A097-D8D7E5C64FCB}
#endif
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=ch998244353
AppPublisherURL=https://github.com/ch998244353/TuckPane
AppSupportURL=https://github.com/ch998244353/TuckPane/issues
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
OutputDir={#OutputDir}
OutputBaseFilename=TuckPane-{#MyAppVersion}-win-x64-setup
SetupIconFile=..\src\TuckPane\Assets\TuckPane.ico
UninstallDisplayIcon={app}\TuckPane.exe
#ifdef UpdateValidation
Compression=lzma2/fast
SolidCompression=no
#else
Compression=lzma2/ultra64
SolidCompression=yes
#endif
WizardStyle=modern
LicenseFile=..\LICENSE
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany=ch998244353
VersionInfoDescription=TuckPane offline installer
VersionInfoProductName=TuckPane
#ifndef UpdateValidation
AppMutex=Local\TuckPane-019d2f2d-0bfb-7ff0-98f5-d93093bb0b5d
#endif
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#WebView2Installer}"; DestDir: "{tmp}"; DestName: "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Flags: deleteafterinstall noencryption nocompression; Check: WebView2RuntimeMissing
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[InstallDelete]
Type: files; Name: "{app}\TuckPane.ShellExtension.dll"

#ifndef UpdateValidation
[Icons]
Name: "{autoprograms}\TuckPane"; Filename: "{app}\TuckPane.exe"
Name: "{autodesktop}\TuckPane"; Filename: "{app}\TuckPane.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "GlassFolder"; Flags: deletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "TuckPane"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.tucknote"; ValueType: string; ValueName: ""; ValueData: "TuckPane.Note"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\TuckPane.Note"; ValueType: string; ValueName: ""; ValueData: "TuckPane Note"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\TuckPane.Note\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Assets\Note.ico,0"
Root: HKCU; Subkey: "Software\Classes\TuckPane.Note\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\TuckPane.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\.tucktodo"; ValueType: string; ValueName: ""; ValueData: "TuckPane.Todo"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\TuckPane.Todo"; ValueType: string; ValueName: ""; ValueData: "TuckPane To-do"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\TuckPane.Todo\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Assets\Todo.ico,0"
Root: HKCU; Subkey: "Software\Classes\TuckPane.Todo\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\TuckPane.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\CLSID\{{3464A79C-8FDA-4922-AFB1-CD37263D1810}"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\DesktopBackground\Shell\0000.TuckPane.CreateOrganizer"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Shell\0000.TuckPane.CreateOrganizerHere"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\DesktopBackground\Shell\TuckPane.CreateNote"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\Shell\TuckPane.CreateNote"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Shell\TuckPane.CreateNote"; Flags: deletekey
#endif

[Run]
Filename: "{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Parameters: "/silent /install"; StatusMsg: "Installing Microsoft Edge WebView2 Runtime... / 正在安装 Microsoft Edge WebView2 Runtime..."; Flags: runhidden waituntilterminated; Check: WebView2RuntimeMissing; AfterInstall: VerifyWebView2Runtime
Filename: "{app}\TuckPane.exe"; Description: "{cm:LaunchProgram,TuckPane}"; Flags: nowait postinstall skipifsilent; Check: WebView2RuntimeInstalled

[Code]
const
  FolderMenuKey = 'Software\Classes\Directory\shell\TuckPane.CreateOrganizerHere';
  FolderMenuPreferenceKey = 'Software\TuckPane\FolderContextMenu';
  DesktopMenuKey = 'Software\Classes\DesktopBackground\Shell\TuckPane.CreateOrganizer';
  DesktopMenuPreferenceKey = 'Software\TuckPane\DesktopContextMenu';

procedure SHChangeNotify(EventId: Integer; Flags: Cardinal; Item1, Item2: Integer);
  external 'SHChangeNotify@shell32.dll stdcall';

function FolderMenuCommand: String;
begin
  Result := '"' + ExpandConstant('{app}\TuckPane.exe') + '" --create-organizer-in "%1"';
end;

procedure RegisterFolderMenu;
var
  Enabled: Cardinal;
  Executable: String;
  Success: Boolean;
begin
  { Missing preference means first installation; an explicit off survives upgrades. }
  if RegQueryDWordValue(HKCU, FolderMenuPreferenceKey, 'Enabled', Enabled) then
    if Enabled = 0 then Exit;
  Executable := ExpandConstant('{app}\TuckPane.exe');
  Success := RegWriteStringValue(HKCU, FolderMenuKey, '', '使用 TuckPane 创建收纳窗');
  Success := RegWriteStringValue(HKCU, FolderMenuKey, 'Icon', '"' + Executable + '",0') and Success;
  Success := RegWriteStringValue(HKCU, FolderMenuKey, 'MultiSelectModel', 'Single') and Success;
  Success := RegWriteStringValue(HKCU, FolderMenuKey, 'OwnerExecutable', Executable) and Success;
  Success := RegWriteStringValue(HKCU, FolderMenuKey + '\command', '', FolderMenuCommand) and Success;
  if Success then begin
    Success := RegWriteStringValue(HKCU, FolderMenuPreferenceKey, 'OwnerExecutable', Executable);
    Success := RegWriteDWordValue(HKCU, FolderMenuPreferenceKey, 'Enabled', 1) and Success;
  end;
  if not Success then
    Log('Folder context menu registration failed. Use System settings in TuckPane to repair it.');
  SHChangeNotify($08000000, 0, 0, 0);
end;

procedure UnregisterOwnedFolderMenu;
var
  Command, Owner: String;
  MayRemovePreference: Boolean;
begin
  { Never use unconditional uninsdeletekey: a portable copy may have taken ownership. }
  MayRemovePreference := not RegKeyExists(HKCU, FolderMenuKey);
  Command := '';
  RegQueryStringValue(HKCU, FolderMenuKey + '\command', '', Command);
  if CompareText(Command, FolderMenuCommand) = 0 then
    MayRemovePreference := RegDeleteKeyIncludingSubkeys(HKCU, FolderMenuKey)
  else if Command = '' then begin
    { Incomplete installation: a missing command may still have our ownership marker. }
    if RegQueryStringValue(HKCU, FolderMenuKey, 'OwnerExecutable', Owner) then
      if CompareText(Owner, ExpandConstant('{app}\TuckPane.exe')) = 0 then
        MayRemovePreference := RegDeleteKeyIncludingSubkeys(HKCU, FolderMenuKey);
  end;
  if MayRemovePreference then SHChangeNotify($08000000, 0, 0, 0);
  if MayRemovePreference and RegQueryStringValue(HKCU, FolderMenuPreferenceKey, 'OwnerExecutable', Owner) then
    if CompareText(Owner, ExpandConstant('{app}\TuckPane.exe')) = 0 then
      RegDeleteKeyIncludingSubkeys(HKCU, FolderMenuPreferenceKey);
end;

function DesktopMenuCommand: String;
begin
  Result := '"' + ExpandConstant('{app}\TuckPane.exe') + '" --create-organizer';
end;

function DesktopMenuOwnedByThisInstall: Boolean;
var
  Command, Owner, Executable: String;
begin
  Result := False;
  Executable := ExpandConstant('{app}\TuckPane.exe');
  Owner := '';
  RegQueryStringValue(HKCU, DesktopMenuKey, 'OwnerExecutable', Owner);
  if (Owner <> '') and (CompareText(Owner, Executable) <> 0) then Exit;
  Command := '';
  RegQueryStringValue(HKCU, DesktopMenuKey + '\command', '', Command);
  if Command <> '' then
    Result := CompareText(Command, DesktopMenuCommand) = 0
  else
    { Only our marker authorizes repair/removal of an incomplete registration. }
    Result := CompareText(Owner, Executable) = 0;
end;

procedure RegisterDesktopMenu;
var
  Enabled: Cardinal;
  Executable, Owner: String;
  Success: Boolean;
begin
  { Missing preference enables both first installs and upgrades from older versions. }
  if RegQueryDWordValue(HKCU, DesktopMenuPreferenceKey, 'Enabled', Enabled) then
    if Enabled = 0 then Exit;
  Executable := ExpandConstant('{app}\TuckPane.exe');
  Owner := '';
  RegQueryStringValue(HKCU, DesktopMenuPreferenceKey, 'OwnerExecutable', Owner);
  if (Owner <> '') and (CompareText(Owner, Executable) <> 0) then Exit;
  { Automatic installation must never take over a portable or other installed copy. }
  if RegKeyExists(HKCU, DesktopMenuKey) then
    if not DesktopMenuOwnedByThisInstall then Exit;

  Success := RegWriteStringValue(HKCU, DesktopMenuKey, 'OwnerExecutable', Executable);
  Success := RegWriteStringValue(HKCU, DesktopMenuKey, '', '新建收纳窗') and Success;
  Success := RegWriteStringValue(HKCU, DesktopMenuKey, 'Icon', '"' + Executable + '",0') and Success;
  Success := RegWriteStringValue(HKCU, DesktopMenuKey + '\command', '', DesktopMenuCommand) and Success;
  if Success then begin
    Success := RegWriteStringValue(HKCU, DesktopMenuPreferenceKey, 'OwnerExecutable', Executable);
    Success := RegWriteDWordValue(HKCU, DesktopMenuPreferenceKey, 'Enabled', 1) and Success;
  end;
  if not Success then
    Log('Desktop context menu registration failed. Use System settings in TuckPane to repair it.');
  SHChangeNotify($08000000, 0, 0, 0);
end;

procedure UnregisterOwnedDesktopMenu;
var
  Owner: String;
  MayRemovePreference: Boolean;
begin
  { The menu may have been claimed by another copy since this installation. }
  MayRemovePreference := not RegKeyExists(HKCU, DesktopMenuKey);
  if DesktopMenuOwnedByThisInstall then
    MayRemovePreference := RegDeleteKeyIncludingSubkeys(HKCU, DesktopMenuKey);
  if MayRemovePreference then SHChangeNotify($08000000, 0, 0, 0);
  if MayRemovePreference and RegQueryStringValue(HKCU, DesktopMenuPreferenceKey, 'OwnerExecutable', Owner) then
    if CompareText(Owner, ExpandConstant('{app}\TuckPane.exe')) = 0 then
      RegDeleteKeyIncludingSubkeys(HKCU, DesktopMenuPreferenceKey);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
#ifndef UpdateValidation
  if CurStep = ssPostInstall then begin
    RegisterFolderMenu;
    RegisterDesktopMenu;
  end;
#endif
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
#ifndef UpdateValidation
  if CurUninstallStep = usUninstall then begin
    UnregisterOwnedFolderMenu;
    UnregisterOwnedDesktopMenu;
  end;
#endif
end;

function HasWebView2Version(RootKey: Integer; SubKey: String): Boolean;
var
  Version: String;
  ParsedVersion: Int64;
begin
  Version := '';
  Result := RegQueryStringValue(RootKey, SubKey, 'pv', Version) and
    StrToVersion(Trim(Version), ParsedVersion) and (ParsedVersion > 0);
end;

function WebView2RuntimeInstalled: Boolean;
var
  ClientKey: String;
begin
  ClientKey := 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  Result := HasWebView2Version(HKLM32, ClientKey) or
    HasWebView2Version(HKCU, ClientKey);
end;

function WebView2RuntimeMissing: Boolean;
begin
  Result := not WebView2RuntimeInstalled;
end;

procedure VerifyWebView2Runtime;
begin
  if WebView2RuntimeMissing then
    RaiseException('Microsoft Edge WebView2 Runtime installation completed, but no installed runtime was detected.');
end;
