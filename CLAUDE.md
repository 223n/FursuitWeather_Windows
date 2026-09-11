# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## このリポジトリについて

FursuitWeather_Windowsは、Webサービス[FursuitWeather](https://fursuit-weather.223n.tech/)のAPIを読むWindowsのデスクトップクライアントです。
本体は別のリポジトリにあり、この作業環境では`C:\dev\223n\FursuitWeather`に置かれています。

実装の判断はすべて`docs/`に根拠があります。
コードを読む前に、その機能に対応する文書を読んでください。
文書とコードが食い違っていたら、どちらが正かを決めてから直してください。

| 文書 | 何が書いてあるか |
| ---- | ---- |
| `docs/stack.md` | 言語とフレームワークを決めた根拠。採らなかった案と、その理由 |
| `docs/architecture.md` | プロジェクトの分け方、依存パッケージ、実装で先に決めたこと |
| `docs/api-client.md` | 本体のAPIを叩くときの制約 |
| `docs/development.md` | 既存のCIとリリース運用にC#を載せるための変更点 |
| `docs/notifications.md` | いつ通知を出すか。抑制の規則と文面。実測の根拠 |
| `docs/update.md` | 更新の3つのモードと、その選び方 |
| `docs/open-questions.md` | 未決の仕様と、確かめていない前提 |

判定そのもの（暑さ指数の計算、レベルの判定、連続活動時間の算出）は本体のAPIが行います。
このクライアントは受け取ったJSONを描くだけです。
**判定ロジックを複製しないでください。**

## コマンド

Node 22以上が要ります。

```bash
npm install
npm run lint          # markdownlintとtextlintの両方
npm run lint:md       # Markdownの書式だけ
npm run lint:ja       # 日本語の書き方だけ
npm run lint:md:fix
npm run lint:ja:fix
```

`.NET`の側は次を使います。

```bash
dotnet build -warnaserror
dotnet test
dotnet test --filter "FullyQualifiedName~ForecastParserTests"   # 単体で走らせる
dotnet format --verify-no-changes
```

配布用のインストーラーは次で作ります。
Inno Setupが要ります。

```powershell
./scripts/build-installer.ps1              # 配布用（164.5MB、5分ほど）
./scripts/build-installer.ps1 -SkipRuntime # 組み立ての確認だけ
```

`package.json`の`lint`に`dotnet format`を混ぜないでください。
`ci.yml`の日本語LintのジョブはUbuntuで動き、`.NET`のSDKがありません。

## 決まっている技術構成

根拠は`docs/stack.md`にあります。

- `.NET 10`（LTS）とWPF。TargetFrameworkは`net10.0-windows10.0.22621.0`（下限はWindows 11 22H2）
- Windows App SDK 2.4.0。`WindowsPackageType=None`の未パッケージで使います
- `FursuitWeather.Core`はTargetFrameworkを`net10.0`のままにし、UIに依存させません。Linuxのランナーでもテストを回すためです
- `FursuitWeather.Widget`が小窓とトレイの両方を1プロセスで受け持ちます

Windows 11のウィジェットボードへの対応は要件から外れました。
そのためMSIX、COMサーバー、Adaptive Cards、署名は要りません。

対抗馬だったTauriは、部分クリックスルーと通知の実装可否で落ちました。
乗り換えを提案するときは、この2つが要件から消えたのかを先に確かめてください。

## 設計上の強い制約

### 透過ウィンドウは子HWNDを持てない

WPFの`AllowsTransparency=True`は、WebView2を含む子HWNDを一切描画しません。
**小窓のUIをHTMLで書く案は成立しません。**
XAMLで書きます。
ClearTypeも無効になり、MicaとAcrylicも使えません。

### 時刻はDateTimeOffsetで受けない

APIの`hours[].time`は`2026-08-15T09:00`のような、タイムゾーンを持たない日本時間の文字列です。
`DateTimeOffset`はオフセットが無いときに実行するマシンのローカルの値を補うため、日本時間の開発機では正しく動き、UTCのCIでだけ9時間ずれます。
`DateTime`で受け、使う直前に`TimeZoneInfo`で解釈します。

### hours配列を添字で扱わない

欠測の時間は配列から除かれるため、1時間ごとの連続を保証しません。
`time`の値で突き合わせます。

### 座標は小数2桁に丸めてから送る

丸める処理はUIの層ではなくHTTPクライアントの層に置きます。
Cloudflareのinvocation logがクエリ文字列を丸めずに記録するため、丸めないと本体の公開している約束が破れます。

## リポジトリ運用の落とし穴

どれも複数のファイルを読まないと気付けないものです。

- **版の単一情報源は`package.json`です。** `release.yml`の`npm version`が書き換えます。`.csproj`に`Version`を直書きして二重管理にしないでください
- **`vars.RUNS_ON`をWindowsのジョブに使い回さないでください。** 既存の3ジョブはLinuxのセルフホストを想定しています。`RUNS_ON_WINDOWS`のような別の変数を作ります
- **zizmorは`advanced-security: false`で動きます。** 指摘が1件でもあるとCIが落ちます。`run:`の中に`${{ secrets.* }}`を直接書くとtemplate injectionとして弾かれるため、必ず`env:`を経由します
- **Dependabotの`nuget`は`groups`の`dependency-type`に対応しません。** 既存のnpmの書き方を写しても黙って効きません。`update-types`で分けます
- **ラベルは`.github/labels.yml`に無いと黙って無視されます。** 新しいラベルを使う前に「ラベルを同期する」ワークフローを動かします
- **`.editorconfig`にMicrosoft既定の内容を貼らないでください。** `end_of_line = crlf`が既存の方針と衝突します。C#向けには`indent_size`と`tab_width`だけを足します

## ブランチとコミット

GitFlowで運用します。
詳しくは`CONTRIBUTING.md`にあります。

```bash
git flow feature start 変更の名前
```

- `develop`と`main`へ直接コミットしません
- 取り込みは`develop`へのPull Requestで行います
- マージはマージコミット（Create a merge commit）です。squashとrebaseは、リリースノートが壊れるため使いません
- コミットメッセージは`[Add/Mod/Fix/Del/Doc]`の接頭辞と日本語で書きます。1行目は50文字程度に収め、理由は空行を挟んだ本文に書きます

### PRのマージで`develop`や`main`を消さない

**headが`develop`か`main`のPRをマージすると、そのブランチ自体が消えます。**
develop→mainや、main→developのPRが該当します。

消える経路が2つあります。

- `gh pr merge --delete-branch`はheadのブランチを消します
- リポジトリの「マージ後にheadを自動で消す」（`delete_branch_on_merge`）が有効です。付けなくても消えます

**ルールセットの`deletion`は止めてくれません。**
`main`のルールセットは`develop`と`main`の両方に`deletion`を掛けていますが、bypassが`RepositoryRole:always`です。
管理者の資格情報では素通りします。
実際に`main`へのforce-pushが`Bypassed rule violations`と出て通った記録があります。

マージする前にheadを確かめます。

```bash
gh pr view 番号 --json headRefName,baseRefName -q '"\(.headRefName) -> \(.baseRefName)"'
```

- **headが`develop`か`main`なら、マージしません。** そういうPRはそもそも作りません
- `develop`と`main`を行き来させるのはリリースのワークフローだけです。headは`release/*`（→`main`）と`merge/*`（→`develop`）になり、消えてよいブランチです
- **`main`は既定のブランチではないため、どちらの経路でも消えます。** いちばん危ないのはmain→developのPRです
- `develop`は既定のブランチであるあいだ、GitHubが削除を拒みます。ただし既定を変えた瞬間に同じ危険にさらされます。これに頼らないでください

## 文書の書き方

`**/*.md`のすべてがCIで検査されます。
このファイルも対象です。
規則は共有設定`@223n/lint-config-ja`にあり、実体は`node_modules/@223n/lint-config-ja/config/`で読めます。

- 文体は「ですます調」です
- **一文一行で書きます**
- 一文は120文字までです
- 全角文字と半角文字の間にスペースを入れません
- ただし`ja-space-around-code`が切ってあるため、**半角の語をコードスパンに入れれば前後のスペースは通ります**
- `MD013`（行の長さ）は無効です。一文一行と噛み合わないためです
- 順序付きリストはすべて`1.`で書きます

`.NET`のように先頭がピリオドの語を裸で書くと、和文の句点として弾かれます。
コードスパンに入れてください。

助詞の連続や冗長表現は警告どまりで、CIは止まりません。
とはいえ`npm run lint`の出力に残さないでください。

`lint:ja:fix`をかけたあとは差分を必ず確かめます。
箇条書きの字下げを壊すことがあります。

## まだ手を付けていないもの

- `package.json`の`description`とルートの`README.md`がテンプレートの内容のままです
- コード署名をしていません。SmartScreenの警告が出ます
- トーストのボタンを置いていません。根拠は`docs/notifications.md`の「まだ足していないもの」にあります
