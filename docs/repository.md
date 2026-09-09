# リポジトリの運用

このリポジトリのGitHubまわりの設定をまとめます。
テンプレート（[repo_template](https://github.com/223n/repo_template)）から引き継いだ内容です。

ブランチの運用とリリースの手順は[README](../README.md)にあります。
貢献の手引きは[CONTRIBUTING.md](../CONTRIBUTING.md)にあります。

## ラベル

IssueとPull Requestのラベルはすべて日本語です。
`.github/labels.yml`が定義で、「ラベルを同期する」ワークフローがリポジトリのラベルをこの内容に揃えます。
ラベルを足したり変えたりするときは、GitHubの画面ではなくこのファイルを変えてください。
ファイルに無いラベルは消えます。

| ラベル | 用途 | 誰が付けるか |
| ---- | ---- | ---- |
| バグ | 期待どおりに動かない | Issueフォーム |
| 機能追加 | 新しい機能や改善の要望 | Issueフォーム |
| ドキュメント | 文書の追加や修正 | ラベラー、人 |
| 質問 | 使い方や仕様についての質問 | Issueフォーム |
| アクセシビリティ | 障害のある人の利用を妨げるもの | 人 |
| 重複 | すでにあるIssueやPull Requestと同じ内容 | 人 |
| 無効 | 内容が正しくない、または対象外 | 人 |
| 対応しない | 対応しないと判断したもの | 人 |
| 初心者向け | はじめて貢献する人に向く課題 | 人 |
| 助けが必要 | 手を貸してほしい課題 | 人 |
| 依存関係 | 依存パッケージやアクションの更新 | Dependabot、ラベラー |
| npm | npmパッケージの更新 | Dependabot |
| GitHub Actions | GitHub Actionsの更新 | Dependabot、ラベラー |
| リリース | リリースの準備と公開 | リリースのワークフロー |
| セキュリティ | 脆弱性やセキュリティに関わる修正 | 人、ラベラー |
| 破壊的変更 | 後方互換性を壊す変更 | 人 |

GitHubが最初から用意する英語のラベル（`bug`や`enhancement`など）は、付いているIssueを保ったまま日本語のラベルに改名されます。
Dependabotが作る既定のラベル（`dependencies`、`javascript`、`github_actions`）も同じように改名されます。
対応は`.github/labels.yml`の`from_name`にあります。

「初心者向け」と「助けが必要」は、GitHubの「Contribute」ページが英語名の`good first issue`と`help wanted`で判定するため、改名するとそこには載らなくなります。
その機能を使うなら、この2つは英語名のまま残してください。

Pull Requestには、変えたファイルとブランチ名から`.github/labeler.yml`の規則でラベルが自動で付きます。

## Dependabot

`.github/dependabot.yml`で、npmの依存とGitHub Actionsのアクションを毎週月曜の朝に確かめます。
Pull Requestは`develop`に向けて開かれ、「依存関係」と「npm」または「GitHub Actions」のラベルが付きます。
npmではminorとpatchの更新が本番用と開発用の2つのPull Requestにまとまり、majorの更新は個別に開かれます。
GitHub Actionsのアクションは、majorも含めてすべて1つのPull Requestにまとまります。

ワークフローが使うアクションはコミットSHAで固定し、版はコメントに書いてあります。
DependabotはSHAとコメントの両方を更新します。

セキュリティ更新は常に既定ブランチ（`main`）に向けて開かれます。
既定ブランチ向けのエントリも書いてあるため、そこにも同じラベルと接頭辞が付きます。

## GitHub Actionsのランナー

ワークフローは既定でGitHubがホストする`ubuntu-latest`で動きます。
セルフホストのランナーがある場合は、リポジトリまたは組織の変数`RUNS_ON`に、ランナーのラベル（例: `self-hosted`）を設定します。
設定は「Settings」→「Secrets and variables」→「Actions」の「Variables」にあるほか、`scripts/setup.ps1 -RunsOn ラベル`でも行えます。
変数が無いときは`ubuntu-latest`に倒れるため、設定しなくても動きます。

セルフホストのランナーには、`git`と`gh`（GitHub CLI）、Dockerが要ります。
Dockerはzizmorの検査（コンテナで動きます）に使います。
Nodeはワークフローが用意します。
公開リポジトリでセルフホストのランナーを使うと、フォークからのPull Requestで任意のコードが動くため、非公開のリポジトリで使ってください。

## ワークフローの一覧

| ファイル | いつ動くか | 何をするか |
| ---- | ---- | ---- |
| `ci.yml` | `main`と`develop`への`push`、Pull Request、手動 | 日本語の文書、ワークフローの構文（actionlint）、ワークフローの安全性（zizmor）を検査します |
| `codeql.yml` | `main`と`develop`への`push`、Pull Request、毎週月曜、手動 | ワークフローの安全性をCodeQLで走査します。結果は「Security」→「Code scanning」に出ます |
| `labels.yml` | `.github/labels.yml`か`.github/workflows/labels.yml`の変更、手動 | リポジトリのラベルを定義に揃えます。Pull Requestでは差分の表示だけです |
| `labeler.yml` | Pull Requestを開いたとき、更新したとき | 変えたファイルとブランチ名からラベルを付けます |
| `release.yml` | 手動 | `develop`からリリースブランチを切り、版を上げ、`main`へのPull Requestを開きます |
| `release-publish.yml` | `release/*`か`hotfix/*`のPull Requestが`main`にマージされたとき | タグを打ち、GitHub Releaseを作り、`main`を`develop`に戻します |
