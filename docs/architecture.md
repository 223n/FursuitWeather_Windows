# 構成

プロジェクトの分け方と、実装のうえで先に決めたことをまとめます。
選定の経緯は[技術選定](stack.md)にあります。

## バージョン

| 項目 | 版 | 根拠 |
| ---- | ---- | ---- |
| .NET | 10.0（LTS） | 2025年11月11日リリース、保守期限は2028年11月14日。.NET 8と9は2026年11月10日に揃って期限切れのため、新規で選ぶ理由がありません |
| TargetFramework | `net10.0-windows10.0.19041.0` | Windows App SDKとWinRTのAPIを呼ぶために要ります。最小OSの指定値は着手時に確かめてください |
| Windows App SDK | 2.4.0（Stable、2026年8月13日） | 1.8系は2026年9月9日で保守終了です。`WindowsPackageType=None`のframework-dependentで使います |
| H.NotifyIcon.Wpf | 2.4.1（2025年12月1日） | `net10.0-windows7.0`に対応します。WinFormsへの参照が要らない独立した実装です |

`.NET Core`という名前の製品はもうありません。
`.NET Core 3.1`の次が`.NET 5`で、このときに`Core`が外れて`.NET`へ統合されました。
Windowsの下限はWindows 11 22H2とします。
Windows 10の一般サポートは2025年10月14日に終了しています。

## プロジェクトの分け方

```text
FursuitWeather_Windows/
├── package.json                    # 版の単一情報源。既存のリリース運用を保つ
├── global.json                     # SDKの版を固定する
├── Directory.Build.props           # package.jsonから版を読む。共通の設定を集約する
├── Directory.Packages.props        # 依存パッケージの版を集約する
├── FursuitWeather.Windows.slnx     # SDK 10 が作る新しい形式のソリューション
├── src/
│   ├── FursuitWeather.Core/        # net10.0（Windowsに依存しない）
│   │   ├── Api/                    # HttpClientによる取得、座標の丸め
│   │   ├── Models/                 # DTO
│   │   ├── Time/                   # タイムゾーンなしの日本時間の扱い
│   │   ├── Forecast/               # 表示する1件の選択、鮮度の判定
│   │   ├── Polling/                # 間隔の制御、復帰の検知、バックオフ（未実装）
│   │   └── Changes/                # 判定の悪化の検知（未実装）
│   └── FursuitWeather.Widget/      # net10.0-windows。小窓とトレイを1プロセスに統合する（未実装）
│       ├── app.manifest            # Per-Monitor V2を宣言する
│       ├── Interop/                # Win32のP/Invoke
│       ├── Resources/              # 配色トークン、バッジのStyle、アイコンのGeometry
│       └── Views/
└── tests/
    └── FursuitWeather.Core.Tests/  # xUnit v3
```

ソリューションは`.sln`ではなく`.slnx`です。
`.NET` SDK 10の`dotnet new sln`が作る形式で、XMLで書かれます。
`dotnet build`や`dotnet format`はこのままのファイル名で受け付けます。

小窓とトレイを別のプロセスに分けません。
取得の重複、設定の共有、自動起動の二重管理という手間が、統合する手間を上回るためです。

### 共有ライブラリの境界

`FursuitWeather.Core`はUIに依存させません。
TargetFrameworkにWindowsの接尾辞を付けず`net10.0`のままにします。
そうすると、Windowsでしかビルドできないアプリ本体と違い、ロジックのテストだけはLinuxのランナーでも回せます。

Coreが持つのは「何を表示すべきか」までです。
「どう見せるか」は持たせません。

## 依存パッケージ

入れるもの。

- `Microsoft.WindowsAppSDK` 2.4.0。通知に使います（`AppNotificationManager`と`AppNotificationBuilder`）
- `H.NotifyIcon.Wpf` 2.4.1。トレイへの常駐に使います

標準ライブラリで足りるため、追加が要らないもの。

- `System.Text.Json`（DTOの読み取り）
- `System.Net.Http`
- `System.Threading.PeriodicTimer`（ポーリング）
- `Microsoft.Win32.SystemEvents`（`PowerModeChanged`）
- `System.Net.NetworkInformation.NetworkChange`

入れないもの。

- `CommunityToolkit.WinUI.Notifications`。NuGetで正式に非推奨になりました
- `Microsoft.Toolkit.Uwp.Notifications`。2022年11月で更新が止まっています
- `Wpf.Ui`。参照実装として読むのは有益です。ただしMicaとAcrylicは`AllowsTransparency`と排他のため、依存はしません

Web上のサンプルは、いまも上の2つを使っているものが多く残っています。

## 実装のうえで先に決めたこと

### ウィンドウ

`WindowStyle=None`、`AllowsTransparency=True`、`ResizeMode=NoResize`、`ShowInTaskbar=False`、`Topmost=False`とします。

ルートの背景は`#01000000`にします。
`Brushes.Transparent`はalphaが0のため、ウィンドウ自身がマウスを受け取れなくなります。

クリックスルーを有効にしたウィンドウは、一切のマウス入力を受け付けません。
**解除する経路を必ず別に用意してください。**
トレイのメニューとグローバルホットキーの両方を勧めます。
有効にする機能だけを作り、戻す手段を忘れると操作できなくなります。

### 位置の保存と復元

`Window.Left`と`Window.Top`は使いません。
Per-Monitor V2の環境で値が壊れており、dotnet/wpfのissue #4127は修正の予定がありません。

`GetWindowRect`と`SetWindowPos`の物理ピクセルで扱います。
保存するときは`MonitorFromWindow`と`GetMonitorInfo`で得たモニターの矩形からの相対の位置にします。
`WM_DPICHANGED`が渡す推奨の矩形は必ず尊重します。

### ポーリング

3つの層で組みます。

1. 30秒ごとのtickで経過を判定します。**壁時計（UTC）の最終取得時刻と単調時刻の両方を見て、どちらかが閾値を超えたら取得します。**壁時計だけだと利用者の時刻の変更で暴発し、単調時刻だけだと再起動でリセットされます
1. `PowerModeChanged`の復帰と`NetworkAddressChanged`は、即時に取り直すきっかけとしてのみ使います。届かなくても層1で必ず復帰します
1. 失敗したときは指数バックオフにジッターを足します

間隔は本体の`display.js`と揃えます。
詳しくは[APIの利用](api-client.md)を見てください。

**`Environment.TickCount64`だけに頼らないでください。**
Windowsでは`.NET 10`以前はスリープ中の時間を含み、`.NET 11`で含まなくなるとMicrosoft Learnに明記されています。
単調時刻だけで周期を組むと、`.NET 11`へ上げた瞬間に「よく眠るノートPCでは何時間も取得が走らない」に変わります。
同じ問題が更新の確認にもあります（[更新の仕組み](update.md)）。

### 通知

`AppNotificationManager`を使います。
Windows App SDKのランタイムをインストーラーが連鎖インストールする前提です。
根拠は[技術選定](stack.md)の「配布方式」にあります。

登録には順序の決まりがあります。

1. `NotificationInvoked`のハンドラーを先に登録します。順序を誤ると、通知の処理のために新しいプロセスが起動します
1. 次に`Register(displayName, iconUri)`を呼びます
1. `AppInstance.GetCurrent().GetActivatedEventArgs()`は`Register()`より後に呼びます

未パッケージのアプリでは、`Register()`が呼び出し元のプロセスをCOMサーバーとして登録し、AppUserModelIDも自動で生成します。
**スタートメニューのショートカットは要りません。**
ショートカットが必須なのは、旧来の`Windows.UI.Notifications`を直に叩く経路のほうです。

ただし表示名とアイコンをシェル任せにすると、exeの名前とシェル既定のアイコンに劣化します。
`Register(displayName, iconUri)`で明示してください。

終了時は`Unregister`を呼びます。
`UnregisterAll`はアンインストールのときだけです。

起動時に`AppNotificationManager.IsSupported`を判定し、偽のときは`Shell_NotifyIcon`のバルーンに退きます。
**この退避の経路は削らないでください。**
ランタイムの連鎖インストールが企業のポリシーなどで失敗した環境では、通知だけが静かに壊れます。

**昇格して実行しないでください。**
管理者権限で動かすと`Show()`が無言で失敗します。

### 自動起動

`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`を使います。
管理者権限が要らず、設定アプリとタスクマネージャーの一覧に出るため、利用者が自分で止められます。

タスクスケジューラは採りません。
「最上位の特権で実行」を付けると通知が動かなくなるためです。

インストーラーはRunキーを登録しません。
アプリ内のトグルが一元して管理します。
二重に管理すると、どちらが正しいかが分からなくなるためです。

利用者は「設定」の「アプリ」の「スタートアップ」やタスクマネージャーからも無効にできます。
レジストリの値の有無だけで画面を描かないでください。

### インストールとアンインストール

インストール先は`%LOCALAPPDATA%\Programs\FursuitWeather`です。
利用者単位で入れ、昇格を求めません。

自動で生成されるAppUserModelIDはexeのパスに紐づくと**推定**しています。
インストール先を後から変えると、通知の設定や履歴を引き継げない可能性があります。

アンインストールで消すものは次の3つです。

- 通知の登録（`UnregisterAll`）
- `HKCU`のRunキーの値
- ショートカット

Windows App SDKのランタイムは消しません。
ほかのアプリが使っている可能性があるためです。

アンインストーラーは本体を非昇格で1回起動し、`UnregisterAll`を呼ばせます。
アプリが壊れて起動できない場合に備え、レジストリを直接消す退避の経路も用意します。

`%LOCALAPPDATA%`は`Program Files`と違い、ACLで保護されません。
同じ利用者の権限で動く任意のプロセスが、実行ファイルを差し替えられます。

### テーマ

WPFのFluentテーマと`ThemeMode`は使いません。
`ThemeMode`は`[Experimental("WPF0001")]`が付いており、将来の削除の可能性が明記されています。
Fluent自体も公式に開発中とされています。
この小窓は標準のコントロールをほとんど使わないため、自前のスタイルのほうが安全です。

### 表示に必ず含めるもの

300×200程度の狭い面ですが、次の2つは場所を確保します。

- 気象データの出典表記。レスポンスの`attribution`から描きます
- Font Awesomeの帰属表記（CC BY 4.0）

理由は[APIの利用](api-client.md)にあります。
