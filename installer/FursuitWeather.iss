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
#ifndef OutputBaseFilename
  #define OutputBaseFilename "FursuitWeather-setup"
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
; 名前は scripts/build-installer.ps1 が決めて渡す。
; ここで組み立てると、できあがりを探す側と食い違ったときに黙って見失う
OutputBaseFilename={#OutputBaseFilename}
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
; 人が入れたとき。完了のページのチェックボックスになる
Filename: "{app}\{#AppExeName}"; Description: "{#AppName} を起動する"; Flags: nowait postinstall skipifsilent
; 更新から入れたとき。
; postinstall は完了のページの部品なので、サイレントでは意味を持たない。
; skipifsilent と skipifnotsilent を並べるのが、サイレントでも起動し直す唯一の正攻法である。
; /RELAUNCH=1 が付いているときだけ動かす。人が /VERYSILENT で入れたときに
; 勝手に起動しないようにするためである。
; この行が走るのは、インストールが最後まで進んだときだけである。
; 途中で止まったときは DeinitializeSetup が起動し直す。済ませたことを NoteRelaunched で残す
Filename: "{app}\{#AppExeName}"; Flags: nowait skipifnotsilent; Check: ShouldRelaunch; BeforeInstall: NoteRelaunched

[Code]
{ 更新から呼ばれたときは、呼び元のプロセスが終わるのを待つ。

  PrivilegesRequired=lowest では restartreplace が効かないと公式に明記されている。
  そのため、実行中の exe を置き換える方法は「相手が自分で終わる」以外に無い。

  呼び元は状態を書いてからインストーラーを起動し、すぐ自分を終える。
  ここで終了を待ってからファイルを触る。AppMutex の判定より前に走らせる必要がある。 }

const
  SYNCHRONIZE = $00100000;
  WAIT_TIMEOUT = $00000102;
  { 呼び元が固まったときに、インストーラーまで固まらせない }
  WAIT_LIMIT_MS = 30000;

var
  { 呼び元のプロセスのハンドル。DeinitializeSetup まで閉じない。
    閉じると PID が別のプロセスへ使い回され、終わったかを取り違えうる }
  CallerHandle: LongWord;
  { [Run] の行で起動し直したか }
  Relaunched: Boolean;

function OpenProcess(dwDesiredAccess: LongWord; bInheritHandle: Boolean; dwProcessId: LongWord): LongWord;
  external 'OpenProcess@kernel32.dll stdcall';

function WaitForSingleObject(hHandle: LongWord; dwMilliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';

function CloseHandle(hObject: LongWord): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

{ 更新から呼ばれたときだけ、終わったあとにアプリを起動し直す。
  人が /VERYSILENT で入れたときに、勝手に起動しないようにするためである }
function ShouldRelaunch(): Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

procedure NoteRelaunched();
begin
  Relaunched := True;
end;

procedure CloseCaller();
begin
  if CallerHandle <> 0 then
  begin
    CloseHandle(CallerHandle);
    CallerHandle := 0;
  end;
end;

procedure WaitForCaller();
var
  Pid: Integer;
  Waited: LongWord;
begin
  Pid := StrToIntDef(ExpandConstant('{param:WAITPID|0}'), 0);
  if Pid <= 0 then
    Exit;

  Log(Format('更新: PID %d の終了を待つ', [Pid]));

  CallerHandle := OpenProcess(SYNCHRONIZE, False, Pid);
  if CallerHandle = 0 then
  begin
    { もう終わっている。開けないこと自体は失敗ではない }
    Log('更新: 呼び元のプロセスは見つからなかった。すでに終わっているとみなす');
    Exit;
  end;

  { ハンドルはここで閉じない。DeinitializeSetup で、呼び元が終わったかをもう一度見る }
  Waited := WaitForSingleObject(CallerHandle, WAIT_LIMIT_MS);

  if Waited = WAIT_TIMEOUT then
    Log('更新: 呼び元が時間内に終わらなかった。上書きに失敗する可能性がある')
  else
    Log('更新: 呼び元が終わった');
end;

{ AppMutex の判定より前に走らせる必要があるため、ここで待つ }
function InitializeSetup(): Boolean;
begin
  WaitForCaller();
  Result := True;
end;

{ 更新から呼ばれたのに、[Run] の行で起動し直せなかったときの受け皿。

  インストールが途中で止まると [Run] は走らない。
  ファイルが掴まれている、空き容量が足りない、といった理由で起きる。
  /SUPPRESSMSGBOXES は Abort/Retry を Abort で答えるため、サイレントのまま中止する。
  呼び元はもう終わっているので、ここで起動しないと利用者が手で起動するまで何も動かない。
  そのあいだ暑さの通知は出ず、更新の確認も走らない。

  中止のときの巻き戻しは、置き換えたファイルを元に戻さない。
  Inno のソースで確かめた。前からあったファイルは utDeleteFile_ExistedBeforeInstall の印が付き、消されも戻されもしない。
  版が混ざって起動できない場合は残るが、黙って消えるよりはよい。
  起動したアプリは狙った版と照らし、途中で終わったことを利用者に知らせる。

  DeinitializeSetup は中止したときも呼ばれる。
  CurStepChanged(ssDone) は使えない。準備の段階で止まったときや再起動が要るときにも来て、
  そのとき [Run] は走らない。 }
procedure DeinitializeSetup();
var
  AppDir, Exe: string;
  ResultCode: Integer;
begin
  if Relaunched or not ShouldRelaunch() then
  begin
    CloseCaller();
    Exit;
  end;

  { 呼び元がまだ動いているなら起動しない。二重に常駐させない }
  if (CallerHandle <> 0) and (WaitForSingleObject(CallerHandle, WAIT_LIMIT_MS) = WAIT_TIMEOUT) then
  begin
    Log('更新: 呼び元がまだ動いているため、起動し直さない');
    CloseCaller();
    Exit;
  end;
  CloseCaller();

  { インストール先が決まる前に終わったときは、app 定数を展開できない }
  try
    AppDir := ExpandConstant('{app}');
  except
    Log('更新: インストール先が決まる前に終わったため、起動し直せない');
    Exit;
  end;

  Exe := AppDir + '\{#AppExeName}';
  if not FileExists(Exe) then
  begin
    Log('更新: 起動し直す本体が無い: ' + Exe);
    Exit;
  end;

  if Exec(Exe, '', AppDir, SW_SHOWNORMAL, ewNoWait, ResultCode) then
    Log('更新: インストールが最後まで進まなかったため、本体を起動し直した')
  else
    Log(Format('更新: 本体を起動し直せなかった。コード %d', [ResultCode]));
end;

{ Windows App SDK のランタイムを連鎖インストールする。

  Inno の [Run] は終了コードを見ないため、失敗しても黙って進む。
  ランタイムが入らないと通知だけが静かに壊れるため、ここで結果を見る。

  すでに新しい版が入っているときは 0x80073D06 が返る。これは失敗ではない。 }
const
  { 0x80073D06 = ERROR_PACKAGE_ALREADY_EXISTS。
    Inno の Integer は符号付き32ビットのため、負の値として書く }
  ERROR_PACKAGE_ALREADY_EXISTS = -2146498810;

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
    Log('ランタイム: 同梱したはずのファイルが無い: ' + Installer);
    Result := False;
    Exit;
  end;

  Log('ランタイム: 実行する: ' + Installer + ' --quiet');
  if not Exec(Installer, '--quiet', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Log('ランタイム: 起動できなかった');
    Result := False;
    Exit;
  end;

  Result := (ResultCode = 0) or (ResultCode = ERROR_PACKAGE_ALREADY_EXISTS);
  { Format の配列引数を次の行へ送らないこと。
    Inno は行頭が [ の行をセクションの見出しとして読むため、Invalid section tag になる }
  if Result then
    Log(Format('ランタイム: 終了コード %d。入った', [ResultCode]))
  else
    Log(Format('ランタイム: 終了コード %d。入らなかった', [ResultCode]));
#else
  { 同梱していないものを成功とみなさない。
    真を返すと、配布に使えないインストーラーが黙って正常終了する }
  Log('ランタイム: 同梱していない。このインストーラーは配布に使えない');
  Result := False;
#endif
end;

{ アンインストールのときに、通知の登録と自動起動の値を本体に消させる。

  UninstallRun のセクションではなくここで呼ぶのは、終了コードを記録するためである。
  あちらは本体が落ちても黙って先へ進むため、
  自動起動の登録が端末に残ったままアンインストールが「成功」する。
  実際にそれが起きた。

  ファイルを消す前に走らせる必要があるため usUninstall で行う。 }
procedure RemoveStartupEntries();
var
  RunKey, ApprovedKey: string;
begin
  { 本体を起動せずにレジストリを直接消す退避の経路。
    docs/architecture.md が「アプリが壊れて起動できない場合に備える」として要求している。

    本体は Windows App SDK のブートストラッパーが ModuleInitializer から走るため、
    ランタイムが無い端末では Main へ到達せずに終了する。
    そのとき --uninstall-cleanup は1行も動かず、自動起動の登録が端末に残り、
    サインインのたびに Windows が消えた exe を起動しようとする。 }
  RunKey := 'Software\Microsoft\Windows\CurrentVersion\Run';
  ApprovedKey := 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';

  if RegValueExists(HKEY_CURRENT_USER, RunKey, '{#AppName}') then
  begin
    if RegDeleteValue(HKEY_CURRENT_USER, RunKey, '{#AppName}') then
      Log('後始末: Run キーの値を直接消した')
    else
      Log('後始末: Run キーの値を消せなかった');
  end;

  if RegValueExists(HKEY_CURRENT_USER, ApprovedKey, '{#AppName}') then
  begin
    if RegDeleteValue(HKEY_CURRENT_USER, ApprovedKey, '{#AppName}') then
      Log('後始末: StartupApproved のフラグを直接消した')
    else
      Log('後始末: StartupApproved のフラグを消せなかった');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  Exe: string;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  { 自動起動を先に消す。本体の起動に頼らない。
    ランタイムが無い端末では本体が Main へ到達せずに終わるため、
    あとに回すと消し残す }
  RemoveStartupEntries();

  { 本体を1回起動して通知の登録を消させる。
    これは best-effort である。起動できなくても後始末は続ける }
  Exe := ExpandConstant('{app}\{#AppExeName}');
  if not FileExists(Exe) then
    Log('後始末: 本体が無いため起動を飛ばす: ' + Exe)
  else if not Exec(Exe, '--uninstall-cleanup', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log('後始末: 本体を起動できなかった')
  else if ResultCode = 0 then
    Log('後始末: 本体による後始末が済んだ')
  else
    Log(Format('後始末: 本体が終了コード %d で失敗した。通知の登録が残っている可能性がある', [ResultCode]));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then
    Exit;

  if InstallRuntime() then
    Exit;

  { 止めないが、実態どおりに伝える。
    ブートストラッパーが ModuleInitializer から走るため、
    ランタイムが無いとアプリは起動そのものができない。
    「通知だけが出ない」と伝えるのは誤りだった }
  MsgBox(
    'Windows App SDK のランタイムを入れられませんでした。' + #13#10 +
    'このままでは FursuitWeather を起動できません。' + #13#10#13#10 +
    '次のいずれかを試してください。' + #13#10 +
    '  ・管理者に確認のうえ、もう一度インストールする' + #13#10 +
    '  ・Microsoft の配布する Windows App Runtime を手で入れる' + #13#10#13#10 +
    '詳しい経緯はインストールのログに残っています。',
    mbError, MB_OK);
end;
