; ============================================================================
; Конфігураційний скрипт Inno Setup для BookHub Pro Suite v3.5 (.NET 9 WPF + PDFium)
; Забезпечує:
;   - Встановлення повного пакету BookHub Suite (BookHub.App.exe + AllReaderBook.exe)
;     в одну спільну директорію
;   - Створення ярликів BookHub Pro та AllReaderBook на Робочому столі і в меню «Пуск»
;   - Реєстрацію автономного рідера AllReaderBook у «Відкрити за допомогою...»
;   - Повну сумісність із тихим встановленням WinGet (`winget install BookHub`)
; ============================================================================

#define MyAppName "BookHub Pro"
#define MyReaderName "AllReaderBook"
#define MyAppVersion "3.5.0"
#define MyAppPublisher "BookHub Open-Source Engineering (Roman Myrovych)"
#define MyAppExeName "BookHub.App.exe"
#define MyReaderExeName "AllReaderBook.exe"
#define SourcePublishDir "Publish\BookHub_InstallerPayload_v3.5.1"
#define SourceInstallerSettings "Publish\BookHub_InstallerSettings_v3.5.1\settings.json"
#define SourceReaderSettings "Publish\BookHub_InstallerSettings_v3.5.1\allreaderbook_settings.json"

[Setup]
AppId={{8D4F67E2-91B5-4C84-A3F1-7B2C5819D4E0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} Suite v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
LicenseFile=LICENSE
InfoBeforeFile=THIRD_PARTY_LICENSES.txt
; Per-user install keeps the app data directory writable without requiring elevation.
DefaultDirName={localappdata}\Programs\BookHub Pro
DisableDirPage=no
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=no
AllowNoIcons=yes
ChangesAssociations=yes
SetupIconFile=BookHub.App\Assets\bookhub.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} Suite
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
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon} ({#MyAppName} + {#MyReaderName})"; GroupDescription: "{cm:AdditionalIcons}"
Name: "assoc_pdf"; Description: "Асоціювати файли .pdf з {#MyReaderName}"; GroupDescription: "Асоціація форматів книг:"
Name: "assoc_epub"; Description: "Асоціювати файли .epub з {#MyReaderName}"; GroupDescription: "Асоціація форматів книг:"
Name: "assoc_fb2"; Description: "Асоціювати файли .fb2 з {#MyReaderName}"; GroupDescription: "Асоціація форматів книг:"

[Dirs]
Name: "{app}\cache"
Name: "{app}\Backups"

[Files]
Source: "{#SourcePublishDir}\*"; DestDir: "{app}"; Excludes: "bookhub.db,settings.json,allreaderbook_settings.json,reader_settings.json"; Flags: ignoreversion recursesubdirs createallsubdirs
; Use the empty, initialized release database only on a fresh install; never replace or uninstall user data.
Source: "{#SourcePublishDir}\bookhub.db"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#SourceInstallerSettings}"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#SourceReaderSettings}"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "THIRD_PARTY_LICENSES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "PRIVACY.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyReaderName}"; Filename: "{app}\{#MyReaderExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyReaderExeName}"
Name: "{group}\Ліцензія (MIT)"; Filename: "{app}\LICENSE"
Name: "{group}\Сторонні компоненти (Third-Party Notices)"; Filename: "{app}\THIRD_PARTY_LICENSES.txt"
Name: "{group}\Політика приватності (PRIVACY)"; Filename: "{app}\PRIVACY.md"
Name: "{group}\Видалити {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{autodesktop}\{#MyReaderName}"; Filename: "{app}\{#MyReaderExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyReaderExeName}"; Tasks: desktopicon

[Registry]
; Register AllReaderBook.exe in "Open With..." for .pdf, .epub, .fb2
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyReaderExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#MyReaderName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyReaderExeName}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyReaderExeName},0"
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyReaderExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyReaderExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyReaderExeName}\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyReaderExeName}\SupportedTypes"; ValueType: string; ValueName: ".epub"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyReaderExeName}\SupportedTypes"; ValueType: string; ValueName: ".fb2"; ValueData: ""

Root: HKCU; Subkey: "Software\Classes\AllReaderBook.Document"; ValueType: string; ValueName: ""; ValueData: "Документ або книга AllReaderBook"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\AllReaderBook.Document\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyReaderExeName},0"
Root: HKCU; Subkey: "Software\Classes\AllReaderBook.Document\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyReaderExeName}"" ""%1"""

Root: HKCU; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "AllReaderBook.Document"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.epub\OpenWithProgids"; ValueType: string; ValueName: "AllReaderBook.Document"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.fb2\OpenWithProgids"; ValueType: string; ValueName: "AllReaderBook.Document"; ValueData: ""; Flags: uninsdeletevalue

Root: HKCU; Subkey: "Software\Classes\.pdf"; ValueType: string; ValueName: ""; ValueData: "AllReaderBook.Document"; Tasks: assoc_pdf
Root: HKCU; Subkey: "Software\Classes\.epub"; ValueType: string; ValueName: ""; ValueData: "AllReaderBook.Document"; Tasks: assoc_epub
Root: HKCU; Subkey: "Software\Classes\.fb2"; ValueType: string; ValueName: ""; ValueData: "AllReaderBook.Document"; Tasks: assoc_fb2

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
