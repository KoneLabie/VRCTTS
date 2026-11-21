#ifndef AppVersion
  #error AppVersion must be supplied by build-installer.ps1
#endif
#ifndef AppSourceDir
  #error AppSourceDir must be supplied by build-installer.ps1
#endif
#ifndef AppNumericVersion
  #error AppNumericVersion must be supplied by build-installer.ps1
#endif
#ifndef InstallerOutputDir
  #error InstallerOutputDir must be supplied by build-installer.ps1
#endif

[Setup]
AppId={{DE909B42-AD4C-4B60-9EB4-C1AE81914235}
AppName=VRCTTS
AppVersion={#AppVersion}
VersionInfoVersion={#AppNumericVersion}
AppPublisher=VRCTTS
AppPublisherURL=https://github.com/KoneLabie/VRCTTS
AppSupportURL=https://github.com/KoneLabie/VRCTTS/issues
AppUpdatesURL=https://github.com/KoneLabie/VRCTTS/releases/latest
DefaultDirName={localappdata}\Programs\VRCTTS
DefaultGroupName=VRCTTS
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.17763
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
OutputDir={#InstallerOutputDir}
OutputBaseFilename=VRCTTS-{#AppVersion}-Setup
UninstallDisplayIcon={app}\VRCTTS.exe
UninstallDisplayName=VRCTTS
AppMutex=Local\VRCTTS.Running
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startmenuicon"; Description: "Create a Start menu shortcut"; GroupDescription: "Shortcuts:"; Flags: checkedonce
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#AppSourceDir}\VRCTTS.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppSourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppSourceDir}\README.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppSourceDir}\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppSourceDir}\DOTNET-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppSourceDir}\DOTNET-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\VRCTTS"; Filename: "{app}\VRCTTS.exe"; WorkingDir: "{app}"; Tasks: startmenuicon
Name: "{userdesktop}\VRCTTS"; Filename: "{app}\VRCTTS.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\VRCTTS.exe"; Description: "Launch VRCTTS"; Flags: nowait postinstall skipifsilent
