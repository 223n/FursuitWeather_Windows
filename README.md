# repo_template

リポジトリのテンプレートです。
日本語の文書の検査、日本語のラベル、Dependabot、GitFlowに沿ったリリースのワークフローが最初から入っています。

## 何が入っているか

| 位置 | 中身 |
| ---- | ---- |
| `.textlintrc.js`、`.markdownlint-cli2.jsonc` | 日本語の文書の検査設定です。規則は公開されている共有設定`@223n/lint-config-ja`にあります |
| `.textlintignore`、`.github/.markdownlint.jsonc` | 検査から外すものと、`.github/`配下だけに効く追加の規則です |
| `package.json` | 検査に使う道具の依存です。版もここで管理します |
| `.github/labels.yml` | IssueとPull Requestのラベルの定義です。すべて日本語です |
| `.github/labeler.yml` | Pull Requestに、変えたファイルやブランチ名からラベルを付ける規則です |
| `.github/dependabot.yml` | Dependabotの設定です。npmとGitHub Actionsを毎週まとめて更新します |
| `.github/release.yml` | GitHub Releaseの本文を自動で作るときの分類です |
| `.github/ISSUE_TEMPLATE/` | Issueのフォームです。バグ報告、機能の要望、質問の3つがあります |
| `.github/pull_request_template.md` | Pull Requestのテンプレートです |
| `.github/CODEOWNERS` | 変更の確認を求める相手です |
| `.github/workflows/` | CI、CodeQL、ラベルの同期、ラベル付け、リリースのワークフローです |
| `scripts/setup.sh` | テンプレートから作った直後の設定をまとめて行うスクリプトです。`gh`を使います |
| `CONTRIBUTING.md` | 貢献の手引きです。ブランチの運用と文書の書き方があります |
| `SECURITY.md` | 脆弱性の報告先です |

## テンプレートから作る

1. GitHubでこのリポジトリを開き、「Use this template」から「Create a new repository」を選びます
1. 「Include all branches」にチェックを入れます。`develop`ブランチも一緒にできます。不要なブランチが複製されていたら消します
1. 作ったリポジトリで、次の「作った直後にやること」を順に行います

### 作った直後にやること

`gh`（GitHub CLI）にログインしたうえで、cloneの中で次を実行します。

```bash
scripts/setup.sh                        # 設定をまとめて行う
scripts/setup.sh --runs-on self-hosted  # セルフホストのランナーも設定する
scripts/setup.sh --dry-run              # 何をするかを表示するだけ
```

スクリプトは次を行います。
何度実行しても結果は同じで、失敗した項目は最後にまとめて表示します。

- `develop`ブランチが無ければ`main`から作ります
- 「Allow GitHub Actions to create and approve pull requests」を有効にします。リリースのワークフローがPull Requestを開くために要ります
- squash mergeとrebase mergeを無効にし、マージ後にブランチを消す設定にします
- Private vulnerability reporting、Dependabot alerts、Dependabot security updatesを有効にします
- 「ラベルを同期する」ワークフローを起動します。既定の英語のラベルが日本語に置き換わります
- `.github/CODEOWNERS`、`.github/ISSUE_TEMPLATE/config.yml`のURL、`package.json`の`name`をこのリポジトリのものに書き換え、`develop`へのPull Requestを開きます

残りは手で行います。

- 組織のリポジトリでは、先に組織の「Settings」→「Actions」→「General」で、ActionsによるPull Requestの作成を許可します。禁止されているとスクリプトの該当項目が失敗します
- `SECURITY.md`に、非公開で連絡できる先を書きます
- `package.json`の`description`と、このREADMEを書き換えます
- ライセンスを変えるなら、`LICENSE`と`package.json`の`license`を書き換えます
- 「Settings」→「Advanced Security」のCode scanningで、Default setupは使いません。走査は`codeql.yml`ワークフローが行います
- `main`と`develop`にブランチ保護をかけます（任意）
  - `develop`にPull Requestを必須にする規則をかけると、リリース後の戻しは毎回Pull Requestになります
  - 「Require code scanning results」の規則は、`codeql.yml`の結果（ツール名はCodeQL）で満たされます

このリポジトリ自身をテンプレートとして使えるようにするには、`scripts/setup.sh --template`を実行するか、「Settings」→「General」の「Template repository」にチェックを入れます。

## 日本語の文書を検査する

Markdownの書式を`markdownlint`で、日本語の書き方を`textlint`で検査します。
規則は公開されている共有設定[@223n/lint-config-ja](https://www.npmjs.com/package/@223n/lint-config-ja)にあり、このリポジトリには「何を検査するか」だけを書いてあります。
規則の理由は[node_japanese_lint_template](https://github.com/223n/node_japanese_lint_template)にあります。

```bash
npm install
npm run lint          # 書式と日本語をまとめて検査する
npm run lint:md:fix   # 書式の指摘を直す
npm run lint:ja:fix   # 日本語の指摘のうち、機械的に直せるものを直す
```

Node 22以上が要ります。

文体は「ですます調」です。
「である調」にしたい場合や、規則を一部だけ変えたい場合は、`.textlintrc.js`のコメントに書き方があります。

`main`と`develop`への`push`と、すべてのPull Requestで、CIが同じ検査をします。
CIではあわせて、ワークフローの構文を`actionlint`で、安全性を`zizmor`で検査します。
ワークフローの安全性は、`codeql.yml`もCodeQLの`actions`言語で走査します。
ワークフローが開いたPull Request（リリースのPull Requestなど）では、CIは「承認待ち」で作られます。
書き込み権限のある人が「Approve workflows to run」を押すと動きます。
承認せずにマージすると、承認待ちの実行は失敗として記録されますが、検査が落ちたわけではありません。

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

## GitHub Actionsのランナー

ワークフローは既定でGitHubがホストする`ubuntu-latest`で動きます。
セルフホストのランナーがある場合は、リポジトリまたは組織の変数`RUNS_ON`に、ランナーのラベル（例: `self-hosted`）を設定します。
設定は「Settings」→「Secrets and variables」→「Actions」の「Variables」にあるほか、`scripts/setup.sh --runs-on ラベル`でも行えます。
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

## ライセンス

Apache License 2.0です。
[LICENSE](LICENSE)を見てください。
