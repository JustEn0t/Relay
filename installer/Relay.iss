#define AppVersion "2.3.1"
#ifndef DistDir
  #define DistDir "..\dist"
#endif
[Setup]
AppId={{6CBCBBF6-E588-428A-8B48-4D8BF2F9A561}
AppName=RELAY
AppVersion={#AppVersion}
AppPublisher=JustEn0t
AppPublisherURL=https://github.com/JustEn0t/Relay
AppSupportURL=https://github.com/JustEn0t/Relay/issues
AppUpdatesURL=https://github.com/JustEn0t/Relay/releases
DefaultDirName={localappdata}\Programs\RELAY
DefaultGroupName=RELAY
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
MinVersion=10.0
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#DistDir}
OutputBaseFilename=RELAY-Setup-{#AppVersion}
SetupIconFile=..\src\Assets\relay.ico
UninstallDisplayIcon={app}\Relay.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=110
LicenseFile=..\LICENSE
AppMutex=Local\EN0TStreamBotV1
CloseApplications=yes
RestartApplications=no
SetupLogging=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#DistDir}\Relay.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\user-guide.txt"; DestDir: "{app}"; DestName: "README.txt"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\RELAY"; Filename: "{app}\Relay.exe"
Name: "{group}\{cm:UninstallProgram,RELAY}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\RELAY"; Filename: "{app}\Relay.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Relay.exe"; Description: "{cm:LaunchProgram,RELAY}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
var Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040);
  if not Result then
    MsgBox('RELAY requires Microsoft .NET Framework 4.8 or later. Install it from https://dotnet.microsoft.com/download/dotnet-framework/net48 and run setup again.', mbError, MB_OK);
end;

// No AppData deletion: uninstall preserves the user's encrypted settings and pending deletion queue.
