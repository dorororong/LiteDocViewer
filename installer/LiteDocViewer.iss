#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\artifacts\App"
#endif

#define AppName "LiteDoc Viewer"
#define AppPublisher "LiteDoc Viewer"
#define AppExeName "LiteDocViewer.exe"

[Setup]
AppId={{E55B1D63-393B-47A9-A1DF-83CA454F9755}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\LiteDoc Viewer
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\Installer
OutputBaseFilename=LiteDocViewer-Setup-x64
SetupIconFile=
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppExeName}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} 온라인 설치 프로그램
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{#AppName} 실행"; Flags: nowait postinstall skipifsilent

[Code]
const
  DotNetDesktopUrl = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';
  DotNetInstallerName = 'windowsdesktop-runtime-8-x64.exe';
  WebView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  WebView2InstallerName = 'MicrosoftEdgeWebView2Setup.exe';
  WebView2ClientKey = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function IsDotNetDesktop8Installed: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(HKLM64,
    'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App',
    Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if Pos('8.', Names[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

function HasValidWebViewVersion(const RootKey: Integer): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(RootKey, WebView2ClientKey, 'pv', Version) and
    (Version <> '') and (Version <> '0.0.0.0');
end;

function IsWebView2Installed: Boolean;
begin
  Result := HasValidWebViewVersion(HKLM32) or HasValidWebViewVersion(HKCU);
end;

function RunPrerequisite(const FileName, Parameters, DisplayName: String;
  var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  WizardForm.StatusLabel.Caption := DisplayName + ' 설치 중...';

  if not Exec(FileName, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := DisplayName + ' 설치 프로그램을 실행할 수 없습니다.';
    Exit;
  end;

  if (ResultCode = 3010) or (ResultCode = 1641) then
    NeedsRestart := True
  else if ResultCode <> 0 then
    Result := Format('%s 설치가 실패했습니다. 종료 코드: %d', [DisplayName, ResultCode]);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  InstallerPath: String;
begin
  Result := '';

  try
    if not IsDotNetDesktop8Installed then
    begin
      WizardForm.StatusLabel.Caption := '.NET 8 Desktop Runtime 다운로드 중...';
      DownloadTemporaryFile(DotNetDesktopUrl, DotNetInstallerName, '', nil);
      InstallerPath := ExpandConstant('{tmp}\' + DotNetInstallerName);
      Result := RunPrerequisite(InstallerPath, '/install /quiet /norestart',
        '.NET 8 Desktop Runtime x64', NeedsRestart);
      if Result <> '' then
        Exit;
    end;

    if not IsWebView2Installed then
    begin
      WizardForm.StatusLabel.Caption := 'Microsoft Edge WebView2 Runtime 다운로드 중...';
      DownloadTemporaryFile(WebView2Url, WebView2InstallerName, '', nil);
      InstallerPath := ExpandConstant('{tmp}\' + WebView2InstallerName);
      Result := RunPrerequisite(InstallerPath, '/silent /install',
        'Microsoft Edge WebView2 Runtime', NeedsRestart);
    end;
  except
    Result := '필수 런타임을 다운로드하거나 설치하지 못했습니다.'#13#10 +
      '인터넷 연결을 확인한 뒤 다시 실행하세요.'#13#10#13#10 + GetExceptionMessage;
  end;
end;
