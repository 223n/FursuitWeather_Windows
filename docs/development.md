# 開発環境とCI

必要な道具と、既存のリポジトリの運用にC#を載せるための変更をまとめます。
着手前に計画として書いた項目は、ほとんど入れ終えています。
まだ入れていないものは、その項に「まだ入れていません」と書いています。

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

3つのStepはどれも済んでいます。
以下は着手前に立てた計画です。
あとで変えたところと、確かめた結果を書き添えています。

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

3つとも、2026年9月9日に実機で確かめました（[未決事項](open-questions.md)の「確かめていない技術的な前提」）。

### Step 2 実際のAPIにつないで常駐させる

`FursuitWeather.Core`にDTOとAPIクライアントを実装します。
DTOは`openapi.yaml`から生成を試み、駄目なら`types.ts`を見て手で書きます。
ポーリングと復帰の検知とバックオフも、この段階で入れます。

ウィンドウの側は、最背面への固定、位置の物理ピクセルでの保存、クリックスルーの切り替えを入れます。
ここまでで「壁紙の上へ置きっぱなしにできる」状態になります。

最背面への固定は、のちにやめて最前面にしました。
実測で、目的と正反対の挙動になると分かったためです（[未決事項](open-questions.md)の「小窓の高さ」）。

### Step 3 トレイと通知

`H.NotifyIcon.Wpf`でトレイに常駐します。
メニューには表示と非表示、クリックスルーの切り替え、設定、終了を置きます。

`AppNotificationManager`を登録し、判定の悪化を通知します。
悪化の定義は、[通知の設計](notifications.md)で決めました。
トーストを出せないときは、小窓とトレイのバルーンで代わりに知らせます。
出せるかどうかは`IsSupported`ではなく、登録できたかと、Windowsの通知の設定が有効かで決めています（`ToastNotifier.IsAvailable`）。

**このStepの最初に、`Register()`が実際に動くかを単体で確かめてください。**
Windows App SDK 2.4.0を未パッケージのWPFで使った実績を、調査では確認できませんでした。
2026年9月9日に確かめ、動きました（[未決事項](open-questions.md)の「確かめていない技術的な前提」）。

## 既存のリポジトリに足すもの

### 最優先

`.gitignore`へ`.NET`の項目を足してあります。
テンプレートには1つも無かったため、C#のプロジェクトを作る前に足しました。

```text
bin/
obj/
.vs/
*.user
TestResults/
```

あわせて`*.pfx`と`*.snk`と`*.p12`も足してあります。
署名の証明書を誤って入れると履歴の書き換えが要り、ブランチ保護と衝突するためです。

### 新しく置いたファイル

- `global.json`。SDKの版を固定します
- `Directory.Build.props`。共通の設定と版の導出を集約します
- `Directory.Packages.props`。依存の版を集約します

`Directory.Build.props`には次を入れてあります。

```xml
<Nullable>enable</Nullable>
<EnableNETAnalyzers>true</EnableNETAnalyzers>
<AnalysisMode>Recommended</AnalysisMode>
<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
```

ロックファイル（`RestorePackagesWithLockFile`と`dotnet restore --locked-mode`）は使いません。
当初は入れる計画でしたが、一度も入れないまま運用しており、2026年9月19日に計画から外しました。
直接の依存の版は`Directory.Packages.props`で固定しています。
取得元は既定のnuget.orgだけで、`nuget.config`は置いていません。
nuget.orgは公開した版の中身を差し替えられないため、ロックファイルで増える守りは小さいと判断しました。

`TreatWarningsAsErrors`はプロジェクトファイルに書かず、CIの`-warnaserror`に任せます。
手元の試行錯誤を止めないためです。

アプリ側のプロジェクトには`Platforms`と`RuntimeIdentifiers`を明示してあります。
Windows App SDKはAnyCPUに対応しません。

### 版の同期

`release.yml`は`package.json`の`version`を単一の情報源にしています。
`prepare`ジョブの`npm version`が書き換え、その差分をコミットします。

`.csproj`との二重管理を避けます。
MSBuildの静的プロパティ関数の許可リストに`System.IO.File::ReadAllText`と`Regex`が入っているため、`Directory.Build.props`から`package.json`を直接読めます。
これで`release.yml`を1行も変えずに済みます。

この読み方は、導入したときに`dotnet msbuild -getProperty:Version`で確かめました。
`0.2.0`と`1.2.0-rc.1`のどちらでも、期待どおりの値になりました。
`Directory.Build.props`を変えたときは、同じコマンドで確かめ直してください。

### ワークフロー

`ci.yml`には`.NET`のジョブが2つあります。

- `build-core`（Linux）。`FursuitWeather.Core`とそのテストだけを、復元・ビルド・テストします。`Core`はWindowsに依存しないため、Linuxで回せます
- `build-windows`（Windows）。WPFを含むソリューション全体を、`dotnet restore`、`dotnet format --verify-no-changes`、`dotnet build -warnaserror`、`dotnet test`の順に回します

書式の検査は`build-windows`だけで行います。
ソリューション全体が対象のため、`Core`の書式もここで検査されます。

**`vars.RUNS_ON`をWindowsのジョブに使い回さないでください。**
`vars.RUNS_ON`は、Linuxのセルフホストのランナーを指す変数です。
`ci.yml`の`lint-ja`、`build-core`、`lint-workflows`、`zizmor`をはじめ、Linuxのジョブはすべてこれを使います。
Windowsのジョブには、別の変数`RUNS_ON_WINDOWS`を使います。

```yaml
runs-on: ${{ vars.RUNS_ON_WINDOWS || 'windows-2025' }}
```

`ci.yml`の`build-windows`と`installer.yml`は、この形で書いてあります。
`installer.yml`は、`windows-2025`のイメージに同梱されたInno Setup 6を使います（[構成](architecture.md)の「組み立て方」）。

C#の走査は、まだ入れていません。
いまの`codeql.yml`が見ているのは、ワークフロー（`language: [actions]`）だけです。
入れるときは、matrixを`include`の形に変え、`csharp`と`build-mode`を足します。
タイムアウトも30分では足りなくなる見込みです。

`zizmor`のジョブは`advanced-security: false`のため、指摘が1件でもあると落ちます。
実際に当たりやすいのは`unpinned-uses`と`cache-poisoning`です。
新しく足すアクションは、既存の慣行どおりコミットSHAで固定し、版はコメントに書いてください。

シークレットは`run:`の中に`${{ secrets.* }}`で直接書かず、`env:`を経由します。
なお`template-injection`の監査が見るのは攻撃者が操作できるコンテキストで、`secrets`は対象外です。
それでも`env:`を経由する運用はGitHubの公式の案内と一致するため、そのまま守ります。
シェルへ渡すときは`${{ env.VAR }}`ではなく`${VAR}`で展開させてください。

Windowsのビルドを必須のチェックにすると、ワークフローが開いたPull RequestのCIが承認待ちになり、`auto_merge`が止まります。
いまは必須にしていません。
`develop`と`main`のルールセットには、必須のステータスチェックの規則がありません。

**インストーラーの生成は`ci.yml`に置いていません。**
`installer.yml`に分け、`release-publish.yml`から呼びます。
組み立てに関わるファイルを変えたPull Requestでも動きますが、必須のチェックにはしていません。
必須にすると、いま述べた`auto_merge`の問題に当たるためです。

### Dependabotとラベル

この項は、まだ入れていません。
いまのDependabotが見ているのは、npmとGitHub Actionsだけです。
入れるときは、次のようにします。

`nuget`のエントリを2つ（`develop`向けと既定ブランチ向け）と、`dotnet-sdk`のエントリを1つ足します。

**`nuget`は`groups`の`dependency-type`に対応しません。**
対応するのはnpmなど一部の生態系だけです。
既存のnpmの書き方（`npm-production`と`npm-development`）をそのまま写しても、黙って効きません。
`update-types`で分けてください。
`cooldown`は`nuget`でも使えます。

`.github/labels.yml`に`NuGet`のラベルを足し、**先に「ラベルを同期する」ワークフローを動かします。**
リポジトリに無いラベルはDependabotが黙って無視します。

`.github/labeler.yml`の「依存関係」の対象へ、`**/*.csproj`、`Directory.Packages.props`、`global.json`を足します。

### 設定ファイル

`.editorconfig`の末尾に足してあります。
既存の`[*]`（2スペース、改行はLF）は変えていません。
より細かいセクションが後勝ちするため衝突しません。

```ini
[*.{cs,csx}]
indent_size = 4
tab_width = 4
```

Microsoftが配る既定の`.editorconfig`をそのまま貼らないでください。
`end_of_line = crlf`と`insert_final_newline = false`が既存の方針と衝突します。

`.gitattributes`には次を足してあります。

```text
*.cmd text eol=crlf
*.bat text eol=crlf
*.pfx binary
*.cer binary
*.snk binary
```

`* text=auto eol=lf`の方針自体は保っています。
gitがコミットのときに正規化するため、Visual StudioがCRLFで書いても実害のある衝突は起きません。

### 手で起動して確かめるときの注意

`Platforms`を指定しているため、**出力先が2系統に分かれます。**

```text
dotnet build <slnx>   → src/FursuitWeather.Widget/bin/x64/Release/...
dotnet build <csproj> → src/FursuitWeather.Widget/bin/Release/...
```

古いほうを掴んで「直したはずの挙動が出ない」と誤診した実例があります。
新しいほうを選ぶか、次のように毎回いちばん新しいものを取ってください。

```powershell
$exe = Get-ChildItem -Recurse -Filter FursuitWeather.Widget.exe src\FursuitWeather.Widget\bin |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
```

`--self-test-clickthrough`を付けて起動すると、クリックスルーを入れた状態で始まります。
猶予で自動的に戻ることを、人が触らずに外から観測できます。

### 日本語Lintとの共存

`package.json`の`lint`に`dotnet format`を混ぜないでください。
`ci.yml`の日本語Lintのジョブ（`lint-ja`）はUbuntuで動き、`.NET`を用意しません。
ソリューションにはWPFのプロジェクトがあり、Linuxではビルドできません。
混ぜるとこのジョブが落ちます。
足すなら`lint`とは別の名前にします。

## インストーラーのビルド

配布方式の根拠は[技術選定](stack.md)の「配布方式」にあります。

定義は`installer/FursuitWeather.iss`に、組み立ての手順は`scripts/build-installer.ps1`にあります。
要点は次のとおりです。

- `AppId`は生成したGUIDを固定し、以後変えません。変えると別のアプリとして二重に入ります
- `AppVersion`には完全なsemverを入れます（`1.2.0-rc.1`）
- `VersionInfoVersion`には4桁の数値を入れます（`1.2.0.0`）
- `PrivilegesRequired=lowest`にします。`PrivilegesRequiredOverridesAllowed`は空欄のままにします
- `CurStepChanged(ssPostInstall)`で`WindowsAppRuntimeInstall.exe --quiet`を実行し、終了コードを確かめます。更新から呼ばれたときは、そのあとで本体を起動し直します

### リリースへの組み込み

`release-publish.yml`は、次の6つのジョブでリリースを公開します。

1. `publish`（Linux）。タグを打ち、GitHub Releaseを下書きで作り、`main`を`develop`へ戻します
1. `installer`（Windows）。`installer.yml`を呼び、インストーラーを組み立ててReleaseへ添えます
1. `manifest`（Linux）。インストーラーの大きさとハッシュから、更新のマニフェスト（`update.json`）を作ります
1. `sign`（Linux）。マニフェストへ署名します。承認が要るのは、このジョブだけです
1. `attach`（Linux）。マニフェストと署名をReleaseへ添えます
1. `finalize`（Linux）。下書きを外して公開します

Releaseは、すべてを添え終えるまで下書きのままです。
途中で落ちても、利用者からは見えません。
署名のジョブを分けた理由は、[更新の仕組み](update.md)の「承認が要るのは署名だけです」にあります。

当初は`publish`・`installer`・`release`の3つに割る計画でした。
下書きで作ってから公開する形と、マニフェストの署名を足したため、いまの6つになっています。

`publish`は、版とタグを`outputs`で後続のジョブへ渡します。
`release.yml`の`prepare`ジョブと同じ形です。

Windowsのランナーの既定のシェルは`pwsh`です。
`installer.yml`は`defaults`を置かず、ステップごとに`shell:`を書いています。
組み立てと後始末の確認は`pwsh`、同梱の確認とReleaseへの添付は`bash`で動きます。

インストーラーの版は、`scripts/build-installer.ps1`が`package.json`から直接読みます。
ここでも、版の単一の情報源は`package.json`です。

`ISCC.exe`は、インストール先の候補とPATHから探します（[構成](architecture.md)の「組み立て方」）。

`v0.4.0`のリリースでは、`installer`のジョブが5分22秒で終わりました。
着手前の推定は3分から6分でした。

## 署名と配布

### 自分の端末だけの段階

署名は要りません。
未パッケージのまま`dotnet run`と`dotnet publish`で動きます。

### 他者へ配る段階

署名は技術選定では解けない問題として残ります。

- 拡張検証（EV）の証明書でもSmartScreenを素通りできなくなりました
- 未署名だとリリースのたびに評判がゼロに戻ります
- Smart App Controlが有効な端末では、起動そのものを止められる可能性があります

第一候補はSignPath Foundationです。
鍵の素材をCIへ置かずに済みます。
ただし申請の条件に「すでにリリース済みであること」が含まれるため、**初回は未署名で出し、そのあと申請し、通ったらCIへ組み込む**という順序になります。

選択肢は次の3つです。

| 手段 | 費用 | 条件 |
| ---- | ---- | ---- |
| Microsoft Store | 無料 | 個人開発者の登録料は2025年9月10日に撤廃されました。認定の通過後にMicrosoftが再署名します |
| SignPath Foundation | 無償 | すべてOSSで、リリース済みで、活発に保守されていることが条件です。初回のリリース後でないと申請できません |
| Certum Open Source | 49ユーロから | 公共料金の請求書などによる実在の確認が要ります。2026年2月27日以降は有効期間が最長459日です |

日本在住の個人はAzure Artifact Signingを使えません。

当面は未署名のままGitHub Releasesに置き、SmartScreenの警告が出ることを[README](../README.md)に書いています。

## 検査の空洞化に注意

`release.yml`のprepareジョブはUbuntuで動き、実行されるのは`npm run lint`（文書の検査）だけです。
WPFはWindowsでしかビルドできないため、アプリのビルドの検証はここに入りません。

緩和策として`FursuitWeather.Core`をUIに依存させず、ロジックのテストだけはUbuntuで回せるようにしてあります（`ci.yml`の`build-core`）。
[構成](architecture.md)のプロジェクトの分け方は、これを意図しています。
