#define MyAppName "TuckPane"
#ifndef MyAppVersion
  #define MyAppVersion "3.0.1"
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
AppId={{2B7D4C50-0148-4D5C-A097-D8D7E5C64FCB}
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
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany=ch998244353
VersionInfoDescription=TuckPane offline installer
VersionInfoProductName=TuckPane
AppMutex=Local\TuckPane-019d2f2d-0bfb-7ff0-98f5-d93093bb0b5d
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
Type: filesandordirs; Name: "{app}\Shell"
Type: filesandordirs; Name: "{app}\ShellPackage"

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
Root: HKCU; Subkey: "Software\Classes\DesktopBackground\Shell\TuckPane.CreateOrganizer"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Shell\TuckPane.CreateOrganizerHere"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\DesktopBackground\Shell\TuckPane.CreateNote"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\Shell\TuckPane.CreateNote"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Shell\TuckPane.CreateNote"; Flags: deletekey

[Run]
Filename: "{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Parameters: "/silent /install"; StatusMsg: "Installing Microsoft Edge WebView2 Runtime... / 正在安装 Microsoft Edge WebView2 Runtime..."; Flags: runhidden waituntilterminated; Check: WebView2RuntimeMissing; AfterInstall: VerifyWebView2Runtime
Filename: "{app}\TuckPane.exe"; Description: "{cm:LaunchProgram,TuckPane}"; Flags: nowait postinstall skipifsilent; Check: WebView2RuntimeInstalled

[Code]
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
