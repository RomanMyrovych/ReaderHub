; ============================================================================
; Конфігураційний скрипт Inno Setup для ReaderHub v3.62 (.NET 9 WPF + PDFium)
; Забезпечує:
;   - Встановлення менеджер-хабу ReaderHub (ReaderHub.exe) та опційного
;     автономного рідера AllReaderBook v0.9.02-beta (AllReaderBook.exe, увімкнено за замовчуванням)
;   - Створення ярликів ReaderHub та AllReaderBook на Робочому столі і в меню «Пуск»
;   - Реєстрацію автономного рідера AllReaderBook у «Відкрити за допомогою...»
;   - Повну сумісність із тихим встановленням WinGet (`winget install ReaderHub`)
; ============================================================================

#define MyAppName "ReaderHub"
#define MyReaderName "AllReaderBook"
#define MyAppVersion "3.62"
#define MyReaderVersion "0.9.02-beta"
#define MyAppPublisher "ReaderHub Open-Source Engineering (Roman Myrovych)"
#define MyAppExeName "ReaderHub.exe"
#define MyReaderExeName "AllReaderBook.exe"
#define SourcePublishDir "Publish\ReaderHub_InstallerPayload_v3.62"
#define SourceInstallerSettings "Publish\ReaderHub_InstallerSettings_v3.62\settings.json"
#define SourceReaderSettings "Publish\ReaderHub_InstallerSettings_v3.62\allreaderbook_settings.json"

[Setup]
AppId={{8D4F67E2-91B5-4C84-A3F1-7B2C5819D4E0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
LicenseFile=LICENSE
InfoBeforeFile=THIRD_PARTY_LICENSES.txt
; Per-user install keeps the app data directory writable without requiring elevation.
DefaultDirName={localappdata}\Programs\ReaderHub
DisableDirPage=no
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=no
AllowNoIcons=yes
ChangesAssociations=yes
SetupIconFile=BookHub.App\Assets\AllReaderBook.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} v{#MyAppVersion}
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
OutputDir=Publish\Installer
OutputBaseFilename=ReaderHub_Setup_v3.62_x64

[Languages]
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; За замовчуванням прапорець увімкнений (True), але користувач може зняти відмітку:
Name: "install_allreaderbook"; Description: "Встановити автономний рідер {#MyReaderName} v{#MyReaderVersion} (рекомендовано)"; GroupDescription: "Компоненти пакету {#MyAppName}:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon} ({#MyAppName} / {#MyReaderName})"; GroupDescription: "{cm:AdditionalIcons}"
Name: "assoc_pdf"; Description: "Асоціювати файли .pdf з {#MyReaderName}"; GroupDescription: "Асоціація форматів книг:"
Name: "assoc_epub"; Description: "Асоціювати файли .epub з {#MyReaderName}"; GroupDescription: "Асоціація форматів книг:"
Name: "assoc_fb2"; Description: "Асоціювати файли .fb2 з {#MyReaderName}"; GroupDescription: "Асоціація форматів книг:"

[Dirs]
Name: "{app}\cache"
Name: "{app}\Backups"
Name: "{app}\libofbooks"

[Files]
Source: "{#SourcePublishDir}\*"; DestDir: "{app}"; Excludes: "AllReaderBook.exe,AllReaderBook.dll,AllReaderBook.deps.json,AllReaderBook.runtimeconfig.json,bookhub.db,settings.json,allreaderbook_settings.json,reader_settings.json"; Flags: ignoreversion recursesubdirs createallsubdirs
; Опційний компонент AllReaderBook (встановлюється за замовчуванням, якщо чекбокс install_allreaderbook = True)
Source: "{#SourcePublishDir}\AllReaderBook.exe"; DestDir: "{app}"; Flags: ignoreversion; Tasks: install_allreaderbook
Source: "{#SourcePublishDir}\AllReaderBook.dll"; DestDir: "{app}"; Flags: ignoreversion; Tasks: install_allreaderbook
Source: "{#SourcePublishDir}\AllReaderBook.deps.json"; DestDir: "{app}"; Flags: ignoreversion; Tasks: install_allreaderbook
Source: "{#SourcePublishDir}\AllReaderBook.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion; Tasks: install_allreaderbook
; Use the empty, initialized release database only on a fresh install; never replace or uninstall user data.
Source: "{#SourcePublishDir}\bookhub.db"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#SourceInstallerSettings}"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#SourceReaderSettings}"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall; Tasks: install_allreaderbook
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "THIRD_PARTY_LICENSES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "PRIVACY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyReaderName}"; Filename: "{app}\{#MyReaderExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyReaderExeName}"; Tasks: install_allreaderbook
Name: "{group}\Ліцензія (MIT)"; Filename: "{app}\LICENSE"
Name: "{group}\Сторонні компоненти (Third-Party Notices)"; Filename: "{app}\THIRD_PARTY_LICENSES.txt"
Name: "{group}\Політика приватності (PRIVACY)"; Filename: "{app}\PRIVACY.md"
Name: "{group}\Видалити {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{autodesktop}\{#MyReaderName}"; Filename: "{app}\{#MyReaderExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyReaderExeName}"; Tasks: desktopicon and install_allreaderbook

[Registry]
; Register AllReaderBook.exe in "Open With..." for .pdf, .epub, .fb2 when installed
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyReaderExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#MyReaderName}"; Flags: uninsdeletekey; Tasks: install_allreaderbook
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyReaderExeName}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyReaderExeName},0"; Tasks: install_allreaderbook
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyReaderExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyReaderExeName}"" ""%1"""; Tasks: install_allreaderbook
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyReaderExeName}\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""; Tasks: install_allreaderbook
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyReaderExeName}\SupportedTypes"; ValueType: string; ValueName: ".epub"; ValueData: ""; Tasks: install_allreaderbook
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyReaderExeName}\SupportedTypes"; ValueType: string; ValueName: ".fb2"; ValueData: ""; Tasks: install_allreaderbook

Root: HKCU; Subkey: "Software\Classes\AllReaderBook.Document"; ValueType: string; ValueName: ""; ValueData: "Документ або книга AllReaderBook"; Flags: uninsdeletekey; Tasks: install_allreaderbook
Root: HKCU; Subkey: "Software\Classes\AllReaderBook.Document\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyReaderExeName},0"; Tasks: install_allreaderbook
Root: HKCU; Subkey: "Software\Classes\AllReaderBook.Document\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyReaderExeName}"" ""%1"""; Tasks: install_allreaderbook

Root: HKCU; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "AllReaderBook.Document"; ValueData: ""; Flags: uninsdeletevalue; Tasks: install_allreaderbook
Root: HKCU; Subkey: "Software\Classes\.epub\OpenWithProgids"; ValueType: string; ValueName: "AllReaderBook.Document"; ValueData: ""; Flags: uninsdeletevalue; Tasks: install_allreaderbook
Root: HKCU; Subkey: "Software\Classes\.fb2\OpenWithProgids"; ValueType: string; ValueName: "AllReaderBook.Document"; ValueData: ""; Flags: uninsdeletevalue; Tasks: install_allreaderbook

Root: HKCU; Subkey: "Software\Classes\.pdf"; ValueType: string; ValueName: ""; ValueData: "AllReaderBook.Document"; Tasks: assoc_pdf and install_allreaderbook
Root: HKCU; Subkey: "Software\Classes\.epub"; ValueType: string; ValueName: ""; ValueData: "AllReaderBook.Document"; Tasks: assoc_epub and install_allreaderbook
Root: HKCU; Subkey: "Software\Classes\.fb2"; ValueType: string; ValueName: ""; ValueData: "AllReaderBook.Document"; Tasks: assoc_fb2 and install_allreaderbook

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
