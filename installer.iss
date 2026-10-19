; ============================================================================
; Конфігураційний скрипт Inno Setup для BookHub Pro v3.5 (.NET 9 WPF + PDFium)
; Забезпечує:
;   - Вибір директорії встановлення (за замовчуванням профіль поточного користувача)
;   - Створення ярликів на Робочому столі та в меню «Пуск»
;   - Повну сумісність із тихим встановленням WinGet (`winget install BookHub`)
;     через прапорці: /SILENT, /VERYSILENT, /SUPPRESSMSGBOXES, /NORESTART, /DIR="..."
; ============================================================================

#define MyAppName "BookHub Pro"
#define MyAppVersion "3.5.0"
#define MyAppPublisher "BookHub Open-Source Engineering"
#define MyAppExeName "BookHub.App.exe"
#define SourcePublishDir "Publish\BookHub_InstallerPayload_v3.5.1"
#define SourceInstallerSettings "Publish\BookHub_InstallerSettings_v3.5.1\settings.json"

[Setup]
AppId={{8D4F67E2-91B5-4C84-A3F1-7B2C5819D4E0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
LicenseFile=LICENSE
InfoBeforeFile=THIRD_PARTY_LICENSES.txt
; Per-user install keeps the app data directory writable without requiring elevation.
DefaultDirName={localappdata}\Programs\BookHub Pro
DisableDirPage=no
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=no
AllowNoIcons=yes
SetupIconFile=BookHub.App\Assets\bookhub.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
OutputDir=Publish\Installer
OutputBaseFilename=BookHubPro_Setup_v3.5_x64

[Languages]
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Dirs]
Name: "{app}\cache"
Name: "{app}\Backups"

[Files]
Source: "{#SourcePublishDir}\*"; DestDir: "{app}"; Excludes: "bookhub.db"; Flags: ignoreversion recursesubdirs createallsubdirs
; Use the empty, initialized release database only on a fresh install; never replace or uninstall user data.
Source: "{#SourcePublishDir}\bookhub.db"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#SourceInstallerSettings}"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "THIRD_PARTY_LICENSES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\Ліцензія (MIT)"; Filename: "{app}\LICENSE"
Name: "{group}\Сторонні компоненти (Third-Party Notices)"; Filename: "{app}\THIRD_PARTY_LICENSES.txt"
Name: "{group}\Видалити {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
