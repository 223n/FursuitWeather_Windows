<#
.SYNOPSIS
テンプレートから作ったリポジトリの初期設定をまとめて行う。

.DESCRIPTION
README の「作った直後にやること」のうち、gh CLI で行えるものを自動化する。
何度実行しても同じ結果になるように書いてあり、途中で失敗した項目は最後にまとめて出す。

前提: gh CLI が入っていて、gh auth login が済んでいること。リポジトリの管理者権限が要る。

.PARAMETER Repo
対象のリポジトリを OWNER/REPO の形で指定する。省略すると、いまいるディレクトリのリポジトリを使う。

.PARAMETER RunsOn
セルフホストのランナーを使うとき、変数 RUNS_ON に設定するラベル（例: self-hosted）。

.PARAMETER Template
このリポジトリ自身をテンプレートとして使えるようにする。

.PARAMETER NoPr
ファイルの書き換えをコミットせず、作業木に残す。

.PARAMETER DryRun
実行せず、何をするかを表示する。

.EXAMPLE
./scripts/setup.ps1
設定をまとめて行う。

.EXAMPLE
./scripts/setup.ps1 -RunsOn self-hosted
セルフホストのランナーも設定する。

.EXAMPLE
./scripts/setup.ps1 -DryRun
何をするかを表示するだけ。
#>
#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^[^/\s]+/[^/\s]+$')]
    [string]$Repo,

    [string]$RunsOn,

    [switch]$Template,

    [switch]$NoPr,

    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$DevelopBranch = 'develop'
$TemplateOwner = '223n'
$TemplateRepo = '223n/repo_template'
$TemplatePackageName = 'repo-template'

$script:Failures = [System.Collections.Generic.List[string]]::new()

function Write-Step { param([string]$Message) Write-Host "▶ $Message" -ForegroundColor Cyan }
function Write-Ok { param([string]$Message) Write-Host "  ✓ $Message" }
function Write-Failure {
    param([string]$Message)
    Write-Host "  ! $Message" -ForegroundColor Yellow
    $script:Failures.Add($Message)
}

<#
.SYNOPSIS
外部コマンドを実行し、成功したかどうかを返す。
.DESCRIPTION
-DryRun のときは実行せず、何をするかだけを表示して成功として扱う。
gh は失敗しても例外を投げないため、$LASTEXITCODE で判定する。
#>
function Invoke-Step {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @()
    )

    if ($DryRun) {
        Write-Host "  + $FilePath $($ArgumentList -join ' ')"
        return $true
    }

    & $FilePath @ArgumentList 2>&1 | Out-Null
    return $LASTEXITCODE -eq 0
}

# ---- 前提を確かめる
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Error 'gh（GitHub CLI）が見つかりません。https://cli.github.com/ から入れてください。'
    exit 1
}

gh auth status 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Error 'gh にログインしていません。gh auth login を実行してください。'
    exit 1
}

if (-not $Repo) {
    $Repo = (gh repo view --json nameWithOwner --jq .nameWithOwner 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $Repo) {
        Write-Error '対象のリポジトリが分かりません。リポジトリの中で実行するか、-Repo OWNER/REPO を付けてください。'
        exit 1
    }
}

$owner, $name = $Repo -split '/', 2
$defaultBranch = gh repo view $Repo --json defaultBranchRef --jq .defaultBranchRef.name

Write-Step "対象: $Repo（既定ブランチ: $defaultBranch）"
if ($DryRun) { Write-Host '  -DryRun のため、実際には何も変えません' }

# ---- 1. マージの方法とブランチの自動削除
Write-Step 'マージはマージコミットだけにし、マージ後にブランチを消す'
$editArgs = @(
    'repo', 'edit', $Repo,
    '--enable-merge-commit',
    '--enable-squash-merge=false',
    '--enable-rebase-merge=false',
    '--delete-branch-on-merge'
)
if ($Template) { $editArgs += '--template' }

if (Invoke-Step -FilePath 'gh' -ArgumentList $editArgs) {
    Write-Ok '設定した'
}
else {
    Write-Failure 'リポジトリの設定を変えられなかった（gh repo edit）'
}

# ---- 2. Actions が PR を開けるようにする
Write-Step 'Actions に Pull Request の作成と承認を許す（リリースのワークフローが使う）'
$args2 = @(
    'api', '--method', 'PUT', "repos/$Repo/actions/permissions/workflow",
    '-f', 'default_workflow_permissions=read',
    '-F', 'can_approve_pull_request_reviews=true',
    '--silent'
)
if (Invoke-Step -FilePath 'gh' -ArgumentList $args2) {
    Write-Ok '許可した'
}
else {
    Write-Failure 'Actions の許可を変えられなかった。組織の設定で禁止されているときは、先に組織の Settings > Actions > General で許可する'
}

# ---- 3. セキュリティ機能
Write-Step 'Private vulnerability reporting を有効にする（SECURITY.md と Issue の選択画面が使う）'
if (Invoke-Step -FilePath 'gh' -ArgumentList @('api', '--method', 'PUT', "repos/$Repo/private-vulnerability-reporting", '--silent')) {
    Write-Ok '有効にした'
}
else {
    Write-Failure 'Private vulnerability reporting を有効にできなかった。組織で一括管理されているか、権限が足りない'
}

Write-Step 'Dependabot alerts と security updates を有効にする'
if (Invoke-Step -FilePath 'gh' -ArgumentList @('api', '--method', 'PUT', "repos/$Repo/vulnerability-alerts", '--silent')) {
    Write-Ok 'Dependabot alerts を有効にした'
}
else {
    Write-Failure 'Dependabot alerts を有効にできなかった'
}

if (Invoke-Step -FilePath 'gh' -ArgumentList @('api', '--method', 'PUT', "repos/$Repo/automated-security-fixes", '--silent')) {
    Write-Ok 'Dependabot security updates を有効にした'
}
else {
    Write-Failure 'Dependabot security updates を有効にできなかった'
}

# ---- 4. develop ブランチ
Write-Step "$DevelopBranch ブランチを用意する"
gh api "repos/$Repo/branches/$DevelopBranch" --silent 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Ok 'すでにある'
}
else {
    $sha = gh api "repos/$Repo/git/ref/heads/$defaultBranch" --jq .object.sha 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $sha) {
        Write-Failure "$defaultBranch の先端が取れず、$DevelopBranch ブランチを作れなかった"
    }
    elseif (Invoke-Step -FilePath 'gh' -ArgumentList @(
            'api', '--method', 'POST', "repos/$Repo/git/refs",
            '-f', "ref=refs/heads/$DevelopBranch",
            '-f', "sha=$sha",
            '--silent')) {
        Write-Ok "$defaultBranch（$($sha.Substring(0, 7))）から作った"
    }
    else {
        Write-Failure "$DevelopBranch ブランチを作れなかった"
    }
}

# ---- 5. セルフホストのランナー
if ($RunsOn) {
    Write-Step "変数 RUNS_ON を $RunsOn にする"
    if (Invoke-Step -FilePath 'gh' -ArgumentList @('variable', 'set', 'RUNS_ON', '--body', $RunsOn, '--repo', $Repo)) {
        Write-Ok '設定した'
    }
    else {
        Write-Failure '変数 RUNS_ON を設定できなかった'
    }
}

# ---- 6. ラベルを揃える
Write-Step '「ラベルを同期する」ワークフローを動かす（既定の英語ラベルが日本語に置き換わる）'
if (Invoke-Step -FilePath 'gh' -ArgumentList @('workflow', 'run', 'labels.yml', '--repo', $Repo, '--ref', $DevelopBranch)) {
    Write-Ok '起動した。結果は Actions の画面で確かめる'
}
else {
    Write-Failure 'ラベル同期を起動できなかった。Actions の画面から「ラベルを同期する」を手で実行する'
}

# ---- 7. テンプレート由来の名前を書き換える
Write-Step 'テンプレート由来の名前を、このリポジトリのものに書き換える'

<#
.SYNOPSIS
ファイルの中の文字列を置き換える。変えたときだけファイル名を返す。
.DESCRIPTION
UTF-8（BOM無し）と改行 LF を保つ。
元の shell 版は node を呼んでいたが、PowerShell では標準の機能で足りるため依存を減らした。
-DryRun のときは書き込まないが、変えるものとして数える（まとめの表示を実際と合わせるため）。
#>
function Update-TemplateName {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$From,
        [Parameter(Mandatory)][string]$To
    )

    # 置き換える意味が無いものは触らない（テンプレートと持ち主が同じ場合など）
    if ($From -ceq $To) { return $null }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }

    $text = [System.IO.File]::ReadAllText($Path)
    if (-not $text.Contains($From)) { return $null }

    if ($DryRun) {
        Write-Host "  + ${Path}: $From → $To"
    }
    else {
        $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
        [System.IO.File]::WriteAllText($Path, $text.Replace($From, $To), $utf8NoBom)
    }

    return $Path
}

$changed = [System.Collections.Generic.List[string]]::new()

if (-not ((Test-Path .git) -and (Test-Path package.json))) {
    Write-Failure 'リポジトリの中で実行していないため、名前の書き換えは飛ばした。clone の中で再実行する'
}
elseif ((git status --porcelain) -join '') {
    # 作業木がきれいなことを確かめる。書き換えを他の変更と混ぜない
    Write-Failure '作業木に未コミットの変更があるため、名前の書き換えは飛ばした。コミットしてから再実行する'
}
else {
    if ($Repo -ne $TemplateRepo) {
        @(
            (Update-TemplateName -Path '.github/CODEOWNERS' -From "@$TemplateOwner" -To "@$owner"),
            (Update-TemplateName -Path '.github/ISSUE_TEMPLATE/config.yml' -From $TemplateRepo -To $Repo),
            # npm のパッケージ名は小文字に限る
            (Update-TemplateName -Path 'package.json' -From """name"": ""$TemplatePackageName""" -To """name"": ""$($name.ToLowerInvariant())""")
        ) | Where-Object { $_ } | ForEach-Object { $changed.Add($_) }
    }

    if ($changed.Count -eq 0) {
        Write-Ok '書き換えるものは無い'
    }
    elseif ($NoPr) {
        Write-Ok "書き換えた（コミットはしていない）: $($changed -join ', ')"
    }
    else {
        $branch = 'feature/setup-repository'
        $pushed = $false

        if ((Invoke-Step -FilePath 'git' -ArgumentList @('switch', '--create', $branch)) -and
            (Invoke-Step -FilePath 'git' -ArgumentList (@('add') + $changed)) -and
            (Invoke-Step -FilePath 'git' -ArgumentList @('commit', '--quiet', '--message', 'テンプレート由来の名前をこのリポジトリのものに書き換える')) -and
            (Invoke-Step -FilePath 'git' -ArgumentList @('push', '--set-upstream', 'origin', $branch))) {
            $pushed = $true
        }

        if (-not $pushed) {
            Write-Failure "書き換えをコミットまたは push できなかった。ブランチ $branch を手で確かめる"
        }
        elseif (Invoke-Step -FilePath 'gh' -ArgumentList @(
                'pr', 'create', '--repo', $Repo, '--base', $DevelopBranch, '--head', $branch,
                '--title', 'テンプレート由来の名前を書き換える',
                '--body', 'scripts/setup.ps1 が CODEOWNERS、Issue の選択画面の URL、package.json の名前を書き換えました。')) {
            Write-Ok 'Pull Request を開いた。確かめてマージする'
        }
        else {
            Write-Failure "Pull Request を開けなかった。ブランチ $branch は push 済み"
        }
    }
}

# ---- まとめ
Write-Host ''
if ($script:Failures.Count -eq 0) {
    Write-Step 'すべて済みました'
}
else {
    Write-Step "手で確かめる項目が $($script:Failures.Count) 件あります"
    foreach ($f in $script:Failures) { Write-Host "  - $f" }
}

Write-Host @'

残りは GitHub の画面で行います。
  - main と develop のルール（Pull Request 必須、Code scanning の結果）: Settings > Rules
  - SECURITY.md に非公開の連絡先を書く
  - package.json の description と README を書き換える
'@

exit ($script:Failures.Count -eq 0 ? 0 : 1)
