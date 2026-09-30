<#
.SYNOPSIS
Windows App SDK のランタイムのインストーラーを取得し、その場所を返す。

.DESCRIPTION
参照している Windows App SDK と同じ系列のものを artifacts/runtime に置く。
取得済みなら、それをそのまま使う。

使う場所は2つある。

1. scripts/build-installer.ps1 が、インストーラーへ同梱する
1. .github/workflows/installer.yml が、後始末の検査の前にランナーへ入れる

取得の手順はここにだけ置く。
片方にだけ手を入れると、同梱したものと検査に使ったものが食い違う。

.PARAMETER Runtime
対象のランタイム識別子。既定は win-x64。

.OUTPUTS
System.String。取得したインストーラーの場所。

.EXAMPLE
./scripts/get-runtime-installer.ps1
x64 のインストーラーを取得し、その場所を返す。
#>
#Requires -Version 7.0
[CmdletBinding()]
[OutputType([string])]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
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
$installer = Join-Path $cacheDir "WindowsAppRuntimeInstall-$channel-$arch.exe"

if (Test-Path $installer) {
    Write-Host "取得済みのランタイムを使います: $installer"
}
else {
    # aka.ms は「その系列の最新の安定版」を指す
    $url = "https://aka.ms/windowsappsdk/$channel/latest/windowsappruntimeinstall-$arch.exe"
    Write-Host "ランタイムを取得しています: $url"
    Invoke-WebRequest -Uri $url -OutFile $installer
}

# 取ってきたものが本当にその系列かを確かめる。
# aka.ms のリダイレクト先が変わっても、黙って別の系列を配らないようにする
$actual = (Get-Item $installer).VersionInfo.FileVersion
if ($actual -and -not $actual.StartsWith($channel)) {
    throw "取得したランタイムの版が $actual で、要求する系列 $channel と違います。artifacts/runtime を消して取り直してください。"
}

$sizeMb = [math]::Round((Get-Item $installer).Length / 1MB, 1)
Write-Host "ランタイム: $actual（$sizeMb MB）"

# 呼び元へ返すのは場所の1つだけにする。
# ほかの値が出力へ流れると、呼び元の変数が配列になる
$installer
