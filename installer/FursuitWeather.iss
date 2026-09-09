; FursuitWeather for Windows のインストーラー。
;
; 組み立て方は scripts/build-installer.ps1 にある。手で ISCC を叩くときは次を渡す。
;
;   ISCC.exe installer\FursuitWeather.iss /DAppVersion=0.3.0 /DPublishDir=... /DRuntimeInstaller=... /DOutputDir=...
;
; 設計の根拠は docs/stack.md の「配布方式」と docs/architecture.md の
; 「インストールとアンインストール」にある。要点は3つ。
;
;   1. 利用者単位で入れ、昇格を求めない（PrivilegesRequired=lowest）
;   2. .NET は self-contained。Desktop Runtime はシステム全体にしか入らず、
;      連鎖させると昇格が必須になるため
;   3. Windows App SDK は framework-dependent。self-contained にすると
;      Singleton パッケージが付いてこず、通知が成立しない。よって連鎖インストールする

#ifndef AppVersion
  #error AppVersion を /DAppVersion=... で渡してください
#endif
#ifndef PublishDir
  #error PublishDir を /DPublishDir=... で渡してください
#endif
#ifndef OutputDir
  #define OutputDir "dist"
#endif

#define AppName "FursuitWeather"
#define AppExeName "FursuitWeather.Widget.exe"
#define AppPublisher "223n"
#define AppUrl "https://github.com/223n/FursuitWeather_Windows"

[Setup]
; AppId は一度決めたら変えない。変えると別のアプリとして二重に入る
AppId={{2D58504D-D7B1-47A0-B2BB-9F7224A3ED43}
AppName={#AppName}
; Inno の AppVersion は自由書式のため、0.3.0-rc.1 のようなプレリリースをそのまま通せる
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
#ifdef VersionInfo
; VersionInfoVersion は数値のみで、AppVersion と違い -rc.1 を受け付けない。
; プレリリースの識別子を落とした値を別に渡す
VersionInfoVersion={#VersionInfo}
#endif
; こちらは自由書式のため、ファイルのプロパティには完全な版を出せる
VersionInfoTextVersion={#AppVersion}

; 昇格を求めない。%LOCALAPPDATA% の下なら書ける
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
; 入れ先を変えさせない。自動で決まる AppUserModelID が exe のパスに紐づくと推定しており、
; あとから動かすと通知の設定と履歴を引き継げない可能性がある
DisableDirPage=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} {#AppVersion}
; 常駐アプリのため、入れ替えの前に必ず閉じてもらう。
; 掴まれたままだと上書きに失敗し、次回の起動で版が混ざる
CloseApplications=yes
CloseApplicationsFilter={#AppExeName}
RestartApplications=no
; 失敗したときに原因を追えるようにする
SetupLogging=yes
LicenseFile={#SourcePath}\..\LICENSE

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Files]
; self-contained の発行結果をまるごと置く
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

#ifdef RuntimeInstaller
; Windows App SDK のランタイム。入れ終わったら消す
Source: "{#RuntimeInstaller}"; DestDir: "{tmp}"; Flags: deleteafterinstall
#endif

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "デスクトップにショートカットを作る"; Flags: unchecked

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{#AppName} を起動する"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 通知の登録と自動起動の値は、ファイルを消すだけでは残る。
; 本体を昇格せずに1回起動して消させる。
; ファイルを消す前に走らせる必要があるため、Flags に waituntilterminated を付ける
Filename: "{app}\{#AppExeName}"; Parameters: "--uninstall-cleanup"; \
  Flags: waituntilterminated runhidden skipifdoesntexist; RunOnceId: "FursuitWeatherCleanup"

[Code]
{ Windows App SDK のランタイムを連鎖インストールする。

  Inno の [Run] は終了コードを見ないため、失敗しても黙って進む。
  ランタイムが入らないと通知だけが静かに壊れるため、ここで結果を見る。

  すでに新しい版が入っているときは 0x80073D06 が返る。これは失敗ではない。 }
const
  ERROR_PACKAGE_ALREADY_EXISTS = $80073D06;

function InstallRuntime(): Boolean;
var
  ResultCode: Integer;
  Installer: string;
begin
  Result := True;
#ifdef RuntimeInstaller
  Installer := ExpandConstant('{tmp}\') + ExtractFileName('{#RuntimeInstaller}');
  if not FileExists(Installer) then
  begin
    Result := False;
    Exit;
  end;

  if not Exec(Installer, '--quiet', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := False;
    Exit;
  end;

  Result := (ResultCode = 0) or (Cardinal(ResultCode) = ERROR_PACKAGE_ALREADY_EXISTS);
#endif
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then
    Exit;

  if InstallRuntime() then
    Exit;

  { 止めない。アプリ自体は動き、通知だけが出なくなる。
    黙って壊れるのが一番悪いので、そのことを伝える }
  MsgBox(
    'Windows App SDK のランタイムを入れられませんでした。' + #13#10 +
    'FursuitWeather は動きますが、トースト通知が出ません。' + #13#10#13#10 +
    'トレイの「検証に使う情報を見る」で状態を確かめられます。',
    mbInformation, MB_OK);
end;
