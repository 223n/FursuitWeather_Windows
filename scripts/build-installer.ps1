<#
.SYNOPSIS
配布用のインストーラーを組み立てる。

.DESCRIPTION
次の3つを順に行う。

1. FursuitWeather.Widget を self-contained で発行する
1. Windows App SDK のランタイムのインストーラーを取得する
1. Inno Setup でひとつの exe にまとめる

版は package.json の version を読む。ここでも .csproj でも版を持たない。

.NET は self-contained、Windows App SDK は framework-dependent という
食い違った組み合わせになっている。根拠は docs/stack.md の「配布方式」にある。
Desktop Runtime はシステム全体にしか入らず連鎖させると昇格が要り、
Windows App SDK は self-contained にすると通知が成立しないためである。

前提: .NET 10 SDK と Inno Setup（6 以上）が入っていること。
入り方で場所が変わるため、探す場所を複数持っている。
windows-2025 のランナーは Program Files (x86) の 6 に、
winget で入れると %LOCALAPPDATA%\Programs の 7 に入る。

.PARAMETER Configuration
ビルドの構成。既定は Release。

.PARAMETER Runtime
発行するランタイム識別子。既定は win-x64。

.PARAMETER OutputDir
できたインストーラーを置く場所。既定はリポジトリ直下の dist。

.PARAMETER SkipRuntime
Windows App SDK のランタイムを同梱しない。
取得に時間がかかるため、組み立ての確認だけをしたいときに使う。
できたインストーラーは配布に使えない。

.PARAMETER RuntimeInstaller
取得済みの WindowsAppRuntimeInstall-x64.exe の場所。
渡すと取得を飛ばす。

.EXAMPLE
./scripts/build-installer.ps1
配布用のインストーラーを作る。

.EXAMPLE
./scripts/build-installer.ps1 -SkipRuntime
ランタイムを同梱せずに、組み立てだけを確かめる。
#>
#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [string]$OutputDir,

    [switch]$SkipRuntime,

    [string]$RuntimeInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/FursuitWeather.Widget/FursuitWeather.Widget.csproj'
$issFile = Join-Path $repoRoot 'installer/FursuitWeather.iss'

if (-not $OutputDir) {
    $OutputDir = Join-Path $repoRoot 'dist'
}

# ---- 版は package.json だけが持つ
$version = (Get-Content (Join-Path $repoRoot 'package.json') -Raw | ConvertFrom-Json).version
if (-not $version) {
    throw 'package.json から version を読めませんでした。'
}
# VersionInfoVersion は数値のみを受け付ける。Directory.Build.props と同じ落とし方をする
$coreVersion = ($version -replace '-.*$', '')
Write-Host "版: $version（数値のみ: $coreVersion.0）"

# ---- 発行
$publishDir = Join-Path $repoRoot "artifacts/publish/$Runtime"
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

Write-Host "発行しています（$Runtime、self-contained）..."
# WindowsAppSDKSelfContained は付けない。
# 付けると Singleton パッケージが同梱されず、通知が動かなくなる
dotnet publish $project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $publishDir `
    -p:PublishSingleFile=false `
    -p:DebugType=none
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish が失敗しました（終了コード $LASTEXITCODE）。"
}

$publishedExe = Join-Path $publishDir 'FursuitWeather.Widget.exe'
if (-not (Test-Path $publishedExe)) {
    throw "発行結果に FursuitWeather.Widget.exe がありません: $publishDir"
}

# ---- Windows App SDK のランタイム
if (-not $SkipRuntime -and -not $RuntimeInstaller) {
    $arch = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64' }

    # 系列は Directory.Packages.props から引く。
    # ここに版を打ち込むと、参照する Windows App SDK を上げたときに追随し損ねる。
    # 未パッケージのブートストラッパーは、参照したのと同じ major.minor の
    # Framework パッケージを要求するため、食い違うと起動そのものが失敗する。
    # 実際に 1.8 を打ち込んだまま 2.4 を参照しており、
    # 開発機に 2 系が入っていたせいで検証をすり抜けた
    $propsPath = Join-Path $repoRoot 'Directory.Packages.props'
    $sdkVersion = ([xml](Get-Content $propsPath -Raw)).Project.ItemGroup.PackageVersion |
        Where-Object { $_.Include -eq 'Microsoft.WindowsAppSDK' } |
        Select-Object -ExpandProperty Version
    if (-not $sdkVersion) {
        throw "Directory.Packages.props から Microsoft.WindowsAppSDK の版を読めませんでした。"
    }
    $channel = ($sdkVersion -split '\.')[0, 1] -join '.'
    Write-Host "Windows App SDK: $sdkVersion（系列 $channel）"

    $cacheDir = Join-Path $repoRoot 'artifacts/runtime'
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
    # 系列をファイル名に入れる。入れないと、系列を上げても古いキャッシュを掴み続ける
    $RuntimeInstaller = Join-Path $cacheDir "WindowsAppRuntimeInstall-$channel-$arch.exe"

    if (Test-Path $RuntimeInstaller) {
        Write-Host "取得済みのランタイムを使います: $RuntimeInstaller"
    }
    else {
        # aka.ms は「その系列の最新の安定版」を指す
        $url = "https://aka.ms/windowsappsdk/$channel/latest/windowsappruntimeinstall-$arch.exe"
        Write-Host "ランタイムを取得しています: $url"
        Invoke-WebRequest -Uri $url -OutFile $RuntimeInstaller
    }

    # 取ってきたものが本当にその系列かを確かめる。
    # aka.ms のリダイレクト先が変わっても、黙って別の系列を配らないようにする
    $actual = (Get-Item $RuntimeInstaller).VersionInfo.FileVersion
    if ($actual -and -not $actual.StartsWith($channel)) {
        throw "取得したランタイムの版が $actual で、要求する系列 $channel と違います。artifacts/runtime を消して取り直してください。"
    }

    $sizeMb = [math]::Round((Get-Item $RuntimeInstaller).Length / 1MB, 1)
    Write-Host "ランタイム: $actual（$sizeMb MB）"
}

# ---- Inno Setup
# 版と入れ方で場所が変わる。決め打ちにすると、片方の環境でだけ動かなくなる。
# windows-2025 のランナーは Program Files (x86) の 6 に、
# winget で入れると %LOCALAPPDATA%\Programs の 7 に入る。
# 新しい版から先に見るため、名前の降順で並べる
$isccCandidates = foreach ($root in @((Join-Path $env:LOCALAPPDATA 'Programs'), ${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
    if (-not $root) { continue }
    Get-ChildItem -Path $root -Filter 'Inno Setup*' -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName 'ISCC.exe' }
}

# Set-StrictMode が効いているため、見つからなかった Get-Command の結果に
# そのまま .Source を付けると失敗する。いったん受けてから見る
$isccOnPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue

$iscc = @($isccCandidates) + @(if ($isccOnPath) { $isccOnPath.Source }) |
    Where-Object { $_ -and (Test-Path $_) } |
    Select-Object -First 1

if (-not $iscc) {
    throw 'Inno Setup の ISCC.exe が見つかりません。winget install JRSoftware.InnoSetup.7 か https://jrsoftware.org/isdl.php から入れてください。'
}
Write-Host "ISCC: $iscc"

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# できあがりの名前はここが決める。
# .iss の側で組み立てると、探す側と食い違ったときに黙って見失う。
# 実際に -noruntime を足したとき、*-setup.exe のフィルタから外れて落ちた
$baseName = "FursuitWeather-$version-x64-setup"

$isccArgs = @(
    $issFile,
    "/DAppVersion=$version",
    "/DVersionInfo=$coreVersion.0",
    "/DPublishDir=$publishDir",
    "/DOutputDir=$OutputDir"
)
if ($RuntimeInstaller) {
    $isccArgs += "/DRuntimeInstaller=$RuntimeInstaller"
}
else {
    # 名前で見分けられるようにする。
    # 同じ名前だと、配れないものが Release へ上がったときに気付けない
    $baseName += '-noruntime'
    Write-Warning 'ランタイムを同梱していません。このインストーラーは配布に使えません。'
}
$isccArgs += "/DOutputBaseFilename=$baseName"

Write-Host '組み立てています...'
& $iscc @isccArgs
if ($LASTEXITCODE -ne 0) {
    throw "ISCC が失敗しました（終了コード $LASTEXITCODE）。"
}

# 名前で決め打ちに取る。
# 更新時刻の新しいものを拾う書き方だと、今回の組み立てが失敗したときに
# 前回の残骸を掴んで成功したように見える
$setupPath = Join-Path $OutputDir "$baseName.exe"
if (-not (Test-Path $setupPath)) {
    throw "できあがったインストーラーが見つかりません: $setupPath"
}
$setup = Get-Item $setupPath

$setupMb = [math]::Round($setup.Length / 1MB, 1)
Write-Host ''
Write-Host "できました: $($setup.FullName)"
Write-Host "大きさ: $setupMb MB"

# GitHub Actions から呼ばれたときは、後続のステップへ場所を渡す
if ($env:GITHUB_OUTPUT) {
    "path=$($setup.FullName)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "name=$($setup.Name)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "size_mb=$setupMb" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}
