# 開発環境とCI

必要な道具と、既存のリポジトリの運用にC#を載せるための変更をまとめます。

## 必要なもの

- .NET SDK 10（`winget install -e --id Microsoft.DotNet.SDK.10`）
- Visual Studio 2026、またはVS CodeとC# Dev Kit
- Node.js 22以上（日本語の文書の検査に使います）

小窓の見た目を詰める作業ではXAMLのデザイナーが効くため、Visual Studioを勧めます。

## 進め方

ブランチの運用はGitFlowです。
詳しくは[CONTRIBUTING.md](../CONTRIBUTING.md)を見てください。

```bash
git flow feature start 変更の名前
```

## 最初のマイルストーン

### Step 1 見た目が破綻しないことを確かめる

手で書いたJSONを描くだけの透過の小窓を1枚作ります。
`WindowStyle=None`、`AllowsTransparency=True`、`#01000000`の背景を指定し、バッジ・活動できる分数・気温の3つを出します。

ここで確かめるのは次の3つです。

1. ClearTypeが無効になった状態の文字が、実機で読めるか
1. alphaが0の余白がクリックを下へ通し、カードの面は掴めるか
1. `app.manifest`のPer-Monitor V2が効いているか（タスクマネージャーの「DPI認識」の列で見えます）

**この3つはどれも後から取り返せない設計の制約です。**
何よりも先に潰してください。
ここで文字が読めなければ、フォントの大きさと太さとコントラストの設計をやり直します。

### Step 2 実際のAPIにつないで常駐させる

`FursuitWeather.Core`にDTOとAPIクライアントを実装します。
DTOは`openapi.yaml`から生成を試み、駄目なら`types.ts`を見て手で書きます。
ポーリングと復帰の検知とバックオフも、この段階で入れます。

ウィンドウの側は、最背面への固定、位置の物理ピクセルでの保存、クリックスルーの切り替えを入れます。
ここまでで「壁紙の上へ置きっぱなしにできる」状態になります。

### Step 3 トレイと通知

`H.NotifyIcon.Wpf`でトレイに常駐します。
メニューには表示と非表示、クリックスルーの切り替え、設定、終了を置きます。

`AppNotificationManager`を登録し、判定の悪化を通知します。
悪化の定義は先に決めてください（[未決事項](open-questions.md)を見てください）。
`IsSupported`が偽のときのバルーンへの退避も入れます。

**このStepの最初に、`Register()`が実際に動くかを単体で確かめてください。**
Windows App SDK 2.4.0を未パッケージのWPFで使った実績を、調査では確認できませんでした。

## 既存のリポジトリに足すもの

### 最優先

`.gitignore`に`.NET`の項目がまったくありません。
C#のプロジェクトを作る前に足します。

```text
bin/
obj/
.vs/
*.user
TestResults/
```

あわせて`*.pfx`と`*.snk`と`*.p12`も足します。
署名の証明書を誤って入れると履歴の書き換えが要り、ブランチ保護と衝突します。

### 新しく置くファイル

- `global.json`。SDKの版を固定します
- `Directory.Build.props`。共通の設定と版の導出を集約します
- `Directory.Packages.props`。依存の版を集約します

`Directory.Build.props`には次を入れます。

```xml
<Nullable>enable</Nullable>
<EnableNETAnalyzers>true</EnableNETAnalyzers>
<AnalysisMode>Recommended</AnalysisMode>
<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
```

`TreatWarningsAsErrors`はプロジェクトファイルに書かず、CIの`-warnaserror`に任せます。
手元の試行錯誤を止めないためです。

アプリ側のプロジェクトには`Platforms`と`RuntimeIdentifiers`を明示します。
Windows App SDKはAnyCPUに対応しません。

### 版の同期

`release.yml`は`package.json`の`version`を単一の情報源にしています。
216行目の`npm version`が書き換え、その差分をコミットします。

`.csproj`との二重管理を避けます。
MSBuildの静的プロパティ関数の許可リストに`System.IO.File::ReadAllText`と`Regex`が入っているため、`Directory.Build.props`から`package.json`を直接読めます。
これで`release.yml`を1行も変えずに済みます。

**この方法は未検証です。**
導入したら`dotnet msbuild -getProperty:Version`で値を必ず確かめてください。

### ワークフロー

`ci.yml`に4つ目のジョブとしてWindowsのビルドを足します。

**`vars.RUNS_ON`を使い回さないでください。**
既存の3つのジョブ（32行目、60行目、83行目）はLinuxのセルフホストを想定しています。
`RUNS_ON_WINDOWS`のような別の変数を新しく作ります。

```yaml
runs-on: ${{ vars.RUNS_ON_WINDOWS || 'windows-latest' }}
```

中身は`dotnet restore --locked-mode`、`dotnet format --verify-no-changes`、`dotnet build -warnaserror`、`dotnet test`の順にします。

`codeql.yml`はmatrixを`language: [actions]`（46行目）から`include`の形に変え、`csharp`と`build-mode`を足します。
タイムアウトも30分では足りなくなる見込みです。

`zizmor`のジョブは`advanced-security: false`（101行目）のため、指摘が1件でもあると落ちます。
シークレットを`run:`の中に`${{ secrets.* }}`で直接書くとtemplate injectionとして指摘されます。
必ず`env:`を経由してください。

Windowsのビルドを必須のチェックにすると、ワークフローが開いたPull RequestのCIが承認待ちになり、`auto_merge`が止まります。
必須にしないか、`auto_merge`を使わないかのどちらかを選びます。

### Dependabotとラベル

`nuget`のエントリを2つ（`develop`向けと既定ブランチ向け）と、`dotnet-sdk`のエントリを1つ足します。

**`nuget`は`groups`の`dependency-type`に対応しません。**
対応するのはnpmなど一部の生態系だけです。
既存のnpmの書き方（`npm-production`と`npm-development`）をそのまま写しても、黙って効きません。
`update-types`で分けてください。
`cooldown`は`nuget`でも使えます。

`.github/labels.yml`に`NuGet`のラベルを足し、**先に「ラベルを同期する」ワークフローを動かします。**
リポジトリに無いラベルはDependabotが黙って無視します。

`.github/labeler.yml`の「依存関係」の対象へ、`**/*.csproj`、`Directory.Packages.props`、`**/packages.lock.json`、`global.json`を足します。

### 設定ファイル

`.editorconfig`の末尾に足します。
既存の`[*]`（2スペース、改行はLF）は変えません。
より細かいセクションが後勝ちするため衝突しません。

```ini
[*.{cs,csx}]
indent_size = 4
tab_width = 4
```

Microsoftが配る既定の`.editorconfig`をそのまま貼らないでください。
`end_of_line = crlf`と`insert_final_newline = false`が既存の方針と衝突します。

`.gitattributes`には次を足します。

```text
*.cmd text eol=crlf
*.bat text eol=crlf
*.pfx binary
*.cer binary
*.snk binary
```

`* text=auto eol=lf`の方針自体は保ちます。
gitがコミットのときに正規化するため、Visual StudioがCRLFで書いても実害のある衝突は起きません。

### 日本語Lintとの共存

`package.json`の`lint`に`dotnet format`を混ぜないでください。
`ci.yml`の日本語Lintのジョブ（29行目から56行目）はUbuntuで動き、.NET SDKがありません。
混ぜるとこのジョブが落ちます。
足すなら`lint`とは別の名前にします。

## 署名と配布

### 自分の端末だけの段階

署名は要りません。
未パッケージのまま`dotnet run`と`dotnet publish`で動きます。

### 他者へ配る段階

署名は技術選定では解けない問題として残ります。

- 拡張検証（EV）の証明書でもSmartScreenを素通りできなくなりました
- 未署名だとリリースのたびに評判がゼロに戻ります
- Smart App Controlが有効な端末では、起動そのものを止められる可能性があります

選択肢は次の3つです。

| 手段 | 費用 | 条件 |
| ---- | ---- | ---- |
| Microsoft Store | 無料 | 個人開発者の登録料は2025年9月10日に撤廃されました。認定の通過後にMicrosoftが再署名します |
| SignPath Foundation | 無償 | すべてOSSで、リリース済みで、活発に保守されていることが条件です。初回のリリース後でないと申請できません |
| Certum Open Source | 49ユーロから | 公共料金の請求書などによる実在の確認が要ります。2026年2月27日以降は有効期間が最長459日です |

日本在住の個人はAzure Artifact Signingを使えません。

当面は「未署名でGitHub Releasesに置き、SmartScreenの警告が出ることをREADMEに明記する」と割り切るかどうかを、別に決める必要があります。

## 検査の空洞化に注意

`release.yml`のprepareジョブはUbuntuで動き、実行されるのは`npm run lint`（文書の検査）だけです。
WPFはWindowsでしかビルドできないため、アプリのビルドの検証はここに入りません。

緩和策として`FursuitWeather.Core`をUIに依存させず、ロジックのテストだけはUbuntuで回せるようにします。
[構成](architecture.md)のプロジェクトの分け方は、これを意図しています。
