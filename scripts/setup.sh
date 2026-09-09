#!/usr/bin/env bash
# テンプレートから作ったリポジトリの初期設定をまとめて行う。
#
# README の「作った直後にやること」のうち、gh CLI で行えるものを自動化する。
# 何度実行しても同じ結果になるように書いてあり、途中で失敗した項目は最後にまとめて出す。
#
# 使い方:
#   scripts/setup.sh [--repo OWNER/REPO] [--runs-on LABEL] [--template] [--no-pr] [--dry-run]
#
#   --repo OWNER/REPO  対象のリポジトリ。省略すると、いまいるディレクトリのリポジトリを使う
#   --runs-on LABEL    セルフホストのランナーを使うとき、変数 RUNS_ON に設定するラベル（例: self-hosted）
#   --template         このリポジトリ自身をテンプレートとして使えるようにする
#   --no-pr            ファイルの書き換えをコミットせず、作業木に残す
#   --dry-run          実行せず、何をするかを表示する
#
# 前提: gh CLI が入っていて、gh auth login が済んでいること。リポジトリの管理者権限が要る。
#       名前の書き換えには Node（このテンプレートの検査にも要る）を使う。
set -euo pipefail

DEVELOP_BRANCH='develop'
TEMPLATE_OWNER='223n'
TEMPLATE_REPO='223n/repo_template'
TEMPLATE_PACKAGE_NAME='repo-template'

repo=''
runs_on=''
make_template=false
open_pr=true
dry_run=false

usage() {
  sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'
}

while [ $# -gt 0 ]; do
  case "$1" in
    --repo) repo="${2:?--repo には OWNER/REPO が要る}"; shift 2 ;;
    --runs-on) runs_on="${2:?--runs-on にはラベルが要る}"; shift 2 ;;
    --template) make_template=true; shift ;;
    --no-pr) open_pr=false; shift ;;
    --dry-run) dry_run=true; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "知らない引数です: $1" >&2; usage >&2; exit 2 ;;
  esac
done

failures=()
info() { printf '\033[36m▶ %s\033[0m\n' "$*"; }
ok() { printf '  ✓ %s\n' "$*"; }
warn() { printf '  ! %s\n' "$*" >&2; failures+=("$*"); }

# コマンドを実行する。--dry-run のときは表示だけにする
run() {
  if $dry_run; then
    printf '  + %s\n' "$*"
    return 0
  fi
  "$@"
}

# ---- 前提を確かめる
if ! command -v gh >/dev/null 2>&1; then
  echo "gh（GitHub CLI）が見つかりません。https://cli.github.com/ から入れてください。" >&2
  exit 1
fi
if ! gh auth status >/dev/null 2>&1; then
  echo "gh にログインしていません。gh auth login を実行してください。" >&2
  exit 1
fi
if [ -z "$repo" ]; then
  if ! repo="$(gh repo view --json nameWithOwner --jq .nameWithOwner 2>/dev/null)"; then
    echo "対象のリポジトリが分かりません。リポジトリの中で実行するか、--repo OWNER/REPO を付けてください。" >&2
    exit 1
  fi
fi
owner="${repo%%/*}"
name="${repo##*/}"
default_branch="$(gh repo view "$repo" --json defaultBranchRef --jq .defaultBranchRef.name)"
info "対象: ${repo}（既定ブランチ: ${default_branch}）"
$dry_run && echo "  --dry-run のため、実際には何も変えません"

# ---- 1. マージの方法とブランチの自動削除
info "マージはマージコミットだけにし、マージ後にブランチを消す"
edit_args=(--enable-merge-commit --enable-squash-merge=false --enable-rebase-merge=false --delete-branch-on-merge)
if $make_template; then
  edit_args+=(--template)
fi
if run gh repo edit "$repo" "${edit_args[@]}"; then
  ok "設定した"
else
  warn "リポジトリの設定を変えられなかった（gh repo edit）"
fi

# ---- 2. Actions が PR を開けるようにする
info "Actions に Pull Request の作成と承認を許す（リリースのワークフローが使う）"
if run gh api --method PUT "repos/${repo}/actions/permissions/workflow" \
  -f default_workflow_permissions=read -F can_approve_pull_request_reviews=true --silent; then
  ok "許可した"
else
  warn "Actions の許可を変えられなかった。組織の設定で禁止されているときは、先に組織の Settings > Actions > General で許可する"
fi

# ---- 3. セキュリティ機能
info "Private vulnerability reporting を有効にする（SECURITY.md と Issue の選択画面が使う）"
if run gh api --method PUT "repos/${repo}/private-vulnerability-reporting" --silent; then
  ok "有効にした"
else
  warn "Private vulnerability reporting を有効にできなかった。組織で一括管理されているか、権限が足りない"
fi

info "Dependabot alerts と security updates を有効にする"
if run gh api --method PUT "repos/${repo}/vulnerability-alerts" --silent; then
  ok "Dependabot alerts を有効にした"
else
  warn "Dependabot alerts を有効にできなかった"
fi
if run gh api --method PUT "repos/${repo}/automated-security-fixes" --silent; then
  ok "Dependabot security updates を有効にした"
else
  warn "Dependabot security updates を有効にできなかった"
fi

# ---- 4. develop ブランチ
info "${DEVELOP_BRANCH} ブランチを用意する"
if gh api "repos/${repo}/branches/${DEVELOP_BRANCH}" --silent >/dev/null 2>&1; then
  ok "すでにある"
else
  if ! sha="$(gh api "repos/${repo}/git/ref/heads/${default_branch}" --jq .object.sha 2>/dev/null)" || [ -z "$sha" ]; then
    warn "${default_branch} の先端が取れず、${DEVELOP_BRANCH} ブランチを作れなかった"
  elif run gh api --method POST "repos/${repo}/git/refs" -f "ref=refs/heads/${DEVELOP_BRANCH}" -f "sha=${sha}" --silent; then
    ok "${default_branch}（${sha:0:7}）から作った"
  else
    warn "${DEVELOP_BRANCH} ブランチを作れなかった"
  fi
fi

# ---- 5. セルフホストのランナー
if [ -n "$runs_on" ]; then
  info "変数 RUNS_ON を ${runs_on} にする"
  if run gh variable set RUNS_ON --body "$runs_on" --repo "$repo"; then
    ok "設定した"
  else
    warn "変数 RUNS_ON を設定できなかった"
  fi
fi

# ---- 6. ラベルを揃える
info "「ラベルを同期する」ワークフローを動かす（既定の英語ラベルが日本語に置き換わる）"
if run gh workflow run labels.yml --repo "$repo" --ref "$DEVELOP_BRANCH"; then
  ok "起動した。結果は Actions の画面で確かめる"
else
  warn "ラベル同期を起動できなかった。Actions の画面から「ラベルを同期する」を手で実行する"
fi

# ---- 7. テンプレート由来の名前を書き換える
info "テンプレート由来の名前を、このリポジトリのものに書き換える"
changed=()
if ! command -v node >/dev/null 2>&1; then
  warn "node が見つからないため、名前の書き換えは飛ばした。Node 22 以上を入れて再実行する"
elif [ -d .git ] && [ -f package.json ]; then
  # 作業木がきれいなことを確かめる。書き換えを他の変更と混ぜない
  if [ -n "$(git status --porcelain)" ]; then
    warn "作業木に未コミットの変更があるため、名前の書き換えは飛ばした。コミットしてから再実行する"
  else
    rewrite() { # rewrite <file> <from> <to>
      local file="$1" from="$2" to="$3"
      if [ -f "$file" ] && grep -qF -- "$from" "$file"; then
        if $dry_run; then
          printf '  + %s: %s → %s\n' "$file" "$from" "$to"
        else
          node -e 'const fs = require("fs"); const [f, a, b] = process.argv.slice(1); fs.writeFileSync(f, fs.readFileSync(f, "utf8").split(a).join(b))' "$file" "$from" "$to"
          changed+=("$file")
        fi
      fi
    }
    if [ "$repo" != "$TEMPLATE_REPO" ]; then
      rewrite .github/CODEOWNERS "@${TEMPLATE_OWNER}" "@${owner}"
      rewrite .github/ISSUE_TEMPLATE/config.yml "$TEMPLATE_REPO" "$repo"
      # npm のパッケージ名は小文字に限る
      rewrite package.json "\"name\": \"${TEMPLATE_PACKAGE_NAME}\"" "\"name\": \"$(printf '%s' "$name" | tr '[:upper:]' '[:lower:]')\""
    fi
    if [ ${#changed[@]} -eq 0 ]; then
      ok "書き換えるものは無い"
    elif $open_pr; then
      branch="feature/setup-repository"
      run git switch --create "$branch"
      run git add "${changed[@]}"
      run git commit --quiet --message "テンプレート由来の名前をこのリポジトリのものに書き換える"
      run git push --set-upstream origin "$branch"
      if run gh pr create --repo "$repo" --base "$DEVELOP_BRANCH" --head "$branch" \
        --title "テンプレート由来の名前を書き換える" \
        --body "scripts/setup.sh が CODEOWNERS、Issue の選択画面の URL、package.json の名前を書き換えました。"; then
        ok "Pull Request を開いた。確かめてマージする"
      else
        warn "Pull Request を開けなかった。ブランチ ${branch} は push 済み"
      fi
    else
      ok "書き換えた（コミットはしていない）: ${changed[*]}"
    fi
  fi
else
  warn "リポジトリの中で実行していないため、名前の書き換えは飛ばした。clone の中で再実行する"
fi

# ---- まとめ
echo
if [ ${#failures[@]} -eq 0 ]; then
  info "すべて済みました"
else
  info "手で確かめる項目が ${#failures[@]} 件あります"
  for f in "${failures[@]}"; do printf '  - %s\n' "$f"; done
fi
cat <<MSG

残りは GitHub の画面で行います。
  - main と develop のルール（Pull Request 必須、Code scanning の結果）: Settings > Rules
  - SECURITY.md に非公開の連絡先を書く
  - package.json の description と README を書き換える
MSG
[ ${#failures[@]} -eq 0 ]
