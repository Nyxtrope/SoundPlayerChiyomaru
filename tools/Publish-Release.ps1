#requires -Version 7
<#
.SYNOPSIS
    配布用の zip を作る。利用者向けの本体と、デバッグ用のシンボル（.pdb）を別々の zip にする。
.DESCRIPTION
    毎回空のフォルダへ発行し直す。確認用の発行先（artifacts/bench など）には、動かしたときの
    settings.json や手元のファイルのパスが入った playlist.m3u8 が残るので、そこからは作らない。
    .pdb は配布しないが、落ちたときのダンプを読むには同じビルドのものが要る（後から作り直せない）。
    シンボルの zip はリリースごとに保管するか、Release に別のファイルとして添える。
.EXAMPLE
    ./tools/Publish-Release.ps1
.EXAMPLE
    ./tools/Publish-Release.ps1 -Version 0.2.0-beta
#>
param(
    # 省略するとプロジェクトの <Version> を使う。
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/SoundPlayerChiyomaru/SoundPlayerChiyomaru.csproj'
$outRoot = Join-Path $root 'artifacts/release'

if (-not $Version) {
    $Version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { throw "$project に <Version> がありません。-Version で指定してください。" }
}

# シンボルはビルドしたコミットと対になる。手元の変更が混ざると、どのコミットとも一致しなくなる。
$commit = git -C $root rev-parse --short HEAD
if (git -C $root status --porcelain) {
    Write-Warning "コミットしていない変更があります。この zip は $commit の内容と一致しません。"
}

# Native AOT のリンクは vswhere.exe で Visual Studio を探す。PATH に無い環境があるので補う。
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue) -and (Test-Path (Join-Path $vsInstaller 'vswhere.exe'))) {
    $env:PATH = "$vsInstaller;$env:PATH"
}

$name = "SoundPlayerChiyomaru-$Version-win-x64"
$appDir = Join-Path $outRoot $name
$symbolsDir = Join-Path $outRoot "$name-symbols"
$appZip = "$appDir.zip"
$symbolsZip = "$symbolsDir.zip"
foreach ($path in $appDir, $symbolsDir, $appZip, $symbolsZip) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}

$publishLog = dotnet publish $project -c Release -r win-x64 -o $appDir -nologo -v q -p:Version=$Version 2>&1
if ($LASTEXITCODE -ne 0) { throw "発行に失敗しました。`n$($publishLog -join "`n")" }
$publishLog | Where-Object { $_ -match 'warning' } | ForEach-Object { Write-Warning $_ }

if (-not (Test-Path -LiteralPath (Join-Path $appDir 'SoundPlayerChiyomaru.exe'))) {
    throw "発行先に SoundPlayerChiyomaru.exe がありません: $appDir"
}

New-Item -ItemType Directory -Path $symbolsDir | Out-Null
Get-ChildItem -LiteralPath $appDir -Filter '*.pdb' -File | Move-Item -Destination $symbolsDir
# 本体にも埋め込んでいるが、zip を展開しただけの人にも見えるように置く。
Copy-Item -LiteralPath (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD-PARTY-NOTICES.txt') -Destination $appDir

# 展開したときに 1 つのフォルダにまとまるよう、フォルダごと入れる。
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($appDir, $appZip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
[System.IO.Compression.ZipFile]::CreateFromDirectory($symbolsDir, $symbolsZip, [System.IO.Compression.CompressionLevel]::Optimal, $true)

Write-Host "バージョン $Version（コミット $commit）"
foreach ($dir in $appDir, $symbolsDir) {
    Write-Host ''
    Write-Host "$(Split-Path $dir -Leaf)/"
    Get-ChildItem -LiteralPath $dir -File | ForEach-Object { Write-Host ('  {0,-32} {1,8:0.0} MB' -f $_.Name, ($_.Length / 1MB)) }
}
Write-Host ''
foreach ($zip in $appZip, $symbolsZip) {
    $item = Get-Item -LiteralPath $zip
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host ('{0}  {1:0.0} MB  SHA256 {2}' -f $item.Name, ($item.Length / 1MB), $hash)
}
