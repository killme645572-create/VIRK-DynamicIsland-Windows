; Установщик Island (Inno Setup 6). Сборка: build-installer.bat в папке проекта.
#define AppName "Island"
#define AppVersion "1.2.0"
#define PublishDir "..\publish"

[Setup]
AppId={{B7C1E6A0-5D3F-4C8E-9A57-2F1D0C6E4B11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Island
DefaultDirName={autopf}\Island
DisableProgramGroupPage=yes
OutputDir=..\installer-output
OutputBaseFilename=Island-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; ставится для текущего пользователя, без запроса прав администратора
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\Island.exe
WizardStyle=modern
; если остров запущен, установщик попросит его закрыть
AppMutex=Island_SingleInstance_Mutex
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "autostart"; Description: "Запускать Island вместе с Windows"
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Island"; Filename: "{app}\Island.exe"
Name: "{autodesktop}\Island"; Filename: "{app}\Island.exe"; Tasks: desktopicon

; то же значение, что использует переключатель автозапуска в самом острове (трей → «Запускать с Windows»)
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Island"; ValueData: """{app}\Island.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\Island.exe"; Description: "Запустить Island"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im Island.exe"; Flags: runhidden; RunOnceId: "KillIsland"
