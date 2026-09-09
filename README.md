# FursuitWeather for Windows

[![CI](https://github.com/223n/FursuitWeather_Windows/actions/workflows/ci.yml/badge.svg)](https://github.com/223n/FursuitWeather_Windows/actions/workflows/ci.yml)

着ぐるみ天気予報[FursuitWeather](https://fursuit-weather.223n.tech/)のWindows向けクライアントです。
デスクトップに常駐し、着ぐるみで活動してよい状況かを目の端で確かめられるようにします。

## 何を作るか

表示する面は2つです。

- 壁紙の上に置く、枠の無い半透明の小窓
- タスクトレイへの常駐と、判定が悪くなったときのトースト通知

判定そのもの（暑さ指数の計算、レベルの判定、連続活動時間の算出）は本体のAPIが行います。
このクライアントは受け取ったJSONを描くだけです。
判定の仕組みは本体の[判定ロジック](https://github.com/223n/FursuitWeather/blob/main/docs/logic.md)にあります。

## 現在の状態

**設計の段階です。実装はまだありません。**

技術選定は終わっています。
`C#`（`.NET 10`）とWPFで作り、Inno Setup 6の利用者単位・非昇格インストーラーで配ります。
決めた理由と、採らなかった案は[docs/](docs/README.md)にまとめてあります。

## ドキュメント

| ドキュメント | 内容 |
| ---- | ---- |
| [技術選定](docs/stack.md) | 開発言語とフレームワークを決めた記録。配布方式 |
| [構成](docs/architecture.md) | プロジェクトの分け方、依存パッケージ、実装で先に決めたこと |
| [APIの利用](docs/api-client.md) | 本体のAPIを叩くときに守ること |
| [開発環境とCI](docs/development.md) | 必要な道具、既存の設定への追加、署名と配布 |
| [更新の仕組み](docs/update.md) | 3つの更新モード、マニフェスト、完全性の検証 |
| [未決事項とリスク](docs/open-questions.md) | まだ決まっていない仕様と、確かめていない前提 |
| [リポジトリの運用](docs/repository.md) | ラベル、Dependabot、ワークフロー、ランナー |

## 開発

### 必要なもの

- Node.js 22以上（日本語の文書の検査に使います）
- `.NET` SDK 10（`winget install -e --id Microsoft.DotNet.SDK.10`）
- Visual Studio 2026、またはVS CodeとC# Dev Kit

`C#`のプロジェクトはまだ無いため、いまはNode.jsだけで足ります。

### 日本語の文書を検査する

Markdownの書式を`markdownlint`で、日本語の書き方を`textlint`で検査します。
規則は公開されている共有設定[@223n/lint-config-ja](https://www.npmjs.com/package/@223n/lint-config-ja)にあり、このリポジトリには「何を検査するか」だけを書いてあります。

```bash
npm install
npm run lint          # 書式と日本語をまとめて検査する
npm run lint:md:fix   # 書式の指摘を直す
npm run lint:ja:fix   # 日本語の指摘のうち、機械的に直せるものを直す
```

文体は「ですます調」です。
書き方の決まりは[CONTRIBUTING.md](CONTRIBUTING.md)にあります。

`main`と`develop`への`push`と、すべてのPull Requestで、CIが同じ検査をします。
CIではあわせて、ワークフローの構文を`actionlint`で、安全性を`zizmor`で検査します。

## ブランチとリリース

GitFlowに沿って運用します。
ブランチの役割は[CONTRIBUTING.md](CONTRIBUTING.md)にあります。

```text
develop ──▶ release/vX.Y.Z ──(Pull Request)──▶ main ──▶ タグ vX.Y.Z と GitHub Release ──▶ develop へ戻す
```

### リリースする

1. Actionsの「リリース」を開き、「Run workflow」を選びます
1. `version`にリリースする版を入れます。`v`は付けません（例: `1.2.0`、`1.2.0-rc.1`）
1. ワークフローが`develop`から`release/vX.Y.Z`ブランチを切り、`package.json`の版を上げ、`main`へのPull Requestを開きます
1. Pull Requestの内容を確かめ、マージコミット（Create a merge commit）でマージします
1. 「リリースを公開する」ワークフローが動き、タグ`vX.Y.Z`を打ち、GitHub Releaseを作り、`main`を`develop`に戻します

版は`package.json`の`version`で管理します。
`develop`と`main`の版、最新のタグのどれよりも大きい版だけを受け付けます。
すでにあるタグや、開いたままの`release/*`ブランチがあると止まります。
`-rc.1`のようなプレリリースの版は、GitHub Releaseでもプレリリースになります。

`auto_merge`を有効にして実行すると、Pull Requestを人手で確かめずにマージし、公開まで一気に進めます。
ただし`main`に必須のチェックや承認のルールがあると、マージで止まります。
ワークフローが開いたPull RequestのCIは承認待ちのままで、ルールを満たせないためです。
その場合は人がPull Requestをマージすれば、公開のワークフローが続きを行います。

`develop`にPull Requestを必須にする規則がある場合、`main`から`develop`への戻しは毎回Pull Requestになります。
ブランチ名は`merge/vX.Y.Z-into-develop`です。
リリースのあとに、このPull Requestもマージコミットでマージしてください。

squashやrebaseでマージしないでください。
リリースノートに`develop`で取り込んだPull Requestが載らず、次の版のPull Requestが衝突します。

GitHub Releaseの本文は、マージしたPull Requestのタイトルとラベルから自動で作られます。
分類は`.github/release.yml`にあります。

### 緊急の修正（hotfix）

リリース済みの内容を急いで直すときは、`main`から`hotfix/名前`ブランチを切ります。
そのブランチで修正し、`package.json`の版も上げます。

```bash
npm version patch --no-git-tag-version
```

`main`へのPull Requestをマージコミットでマージすると、「リリースを公開する」ワークフローが`release/*`と同じように動きます。
版を上げ忘れると、同じ版のタグがすでにあるため止まります。

## データ出典・免責

気象データの出典と利用条件は本体に従います。
[Weather data by Open-Meteo.com](https://open-meteo.com/)（CC BY 4.0、気象庁MSM/GSMモデル由来、無料APIの利用規約により非商用）です。
レスポンスに含まれる出典表記を、画面の見える位置に必ず描きます。

本予報は目安であり、安全を保証するものではありません。
着ぐるみ活動は必ず2人以上で行い、体調の変化を感じたら直ちに中止してください。

## ライセンス

Apache License 2.0です。
[LICENSE](LICENSE)を見てください。
