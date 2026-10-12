#requires -Version 7
<#
.SYNOPSIS
    アプリを発行し、音声ファイルを実際に再生して起動時間・メモリ・CPU を実測する（SPEC 7 章の非機能要件の確認用）。
.DESCRIPTION
    既定では Native AOT で発行する。-Configs jit を付けると、比較用に通常の .NET 版（ランタイム別途）も測る。
    -AudioPath を省略すると、テストが artifacts/test-audio に生成した各形式のファイルを順に再生する
    （先に dotnet test を実行しておくこと。2 秒の短いファイルなので -Seconds 1 と組み合わせる）。
.EXAMPLE
    ./tools/Measure-Startup.ps1 -AudioPath C:\music\test.mp3
.EXAMPLE
    ./tools/Measure-Startup.ps1 -Runs 1 -Seconds 1
#>
param(
    [string[]] $AudioPath,
    [int] $Runs = 5,
    [int] $Seconds = 5,
    [ValidateSet('aot', 'jit')] [string[]] $Configs = @('aot')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/SoundPlayerChiyomaru/SoundPlayerChiyomaru.csproj'
$outRoot = Join-Path $root 'artifacts/bench'

$publishArgs = @{
    'aot' = @()
    'jit' = @('-p:PublishAot=false', '-p:SelfContained=false')
}

if (-not $AudioPath) {
    $AudioPath = Get-ChildItem (Join-Path $root 'artifacts/test-audio') -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.wav', '.mp3', '.m4a', '.flac' -and $_.BaseName -ne 'garbage' } |
        ForEach-Object FullName
    if (-not $AudioPath) { throw 'artifacts/test-audio に音声ファイルがありません。先に dotnet test を実行するか、-AudioPath を指定してください。' }
}
# -LiteralPath でないと、角括弧を含むファイル名がワイルドカードと解釈されて黙って対象から外れる。
$AudioPath = @($AudioPath | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })

# Native AOT のリンクは vswhere.exe で Visual Studio を探す。PATH に無い環境があるので補う。
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue) -and (Test-Path (Join-Path $vsInstaller 'vswhere.exe'))) {
    $env:PATH = "$vsInstaller;$env:PATH"
}

function Get-Median([double[]] $values) {
    $sorted = @($values | Sort-Object)
    $mid = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { $sorted[$mid] } else { ($sorted[$mid - 1] + $sorted[$mid]) / 2 }
}

$summary = foreach ($name in $Configs) {
    $outDir = Join-Path $outRoot $name
    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
    $publishLog = dotnet publish $project -c Release -r win-x64 -o $outDir -nologo -v q @($publishArgs[$name]) 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$name の発行に失敗しました。`n$($publishLog -join "`n")" }
    $publishLog | Where-Object { $_ -match 'warning' } | ForEach-Object { Write-Warning $_ }

    $exe = Join-Path $outDir 'SoundPlayerChiyomaru.exe'
    $resultPath = Join-Path $outDir 'bench-result.json'
    $sizeMb = (Get-ChildItem $outDir -Recurse -File | Where-Object Extension -ne '.pdb' | Measure-Object Length -Sum).Sum / 1MB

    foreach ($audio in $AudioPath) {
        $results = for ($i = 1; $i -le $Runs; $i++) {
            if (Test-Path $resultPath) { [IO.File]::Delete($resultPath) }
            $process = Start-Process $exe -ArgumentList "`"$audio`"", '--bench', "`"$resultPath`"", '--bench-seconds', $Seconds -PassThru
            if (-not $process.WaitForExit(($Seconds + 30) * 1000)) {
                $process.Kill()
                throw "$name / $(Split-Path $audio -Leaf) の $i 回目が終了しませんでした。"
            }
            if (-not (Test-Path $resultPath)) { throw "$name / $(Split-Path $audio -Leaf) の $i 回目が結果を出力しませんでした（終了コード $($process.ExitCode)）。" }
            $result = Get-Content $resultPath -Raw | ConvertFrom-Json
            if ($result.error) { throw "$name / $(Split-Path $audio -Leaf) の $i 回目で再生エラー: $($result.error)" }
            if ($result.playCalledMs -lt 0) { throw "$name / $(Split-Path $audio -Leaf) の $i 回目で再生が始まりませんでした。" }
            $result
        }

        [pscustomobject]@{
            Config        = $name
            File          = Split-Path $audio -Leaf
            SizeMb        = [math]::Round($sizeMb, 1)
            ShownFirstMs  = @($results)[0].shownMs
            ShownMedianMs = Get-Median $results.shownMs
            PlayMedianMs  = Get-Median $results.playCalledMs
            WorkingSetMb  = Get-Median $results.workingSetMb
            PrivateMb     = Get-Median $results.privateMb
            CpuPercent    = Get-Median $results.cpuPercentOfOneCore
            PositionSec   = Get-Median $results.sourcePositionSeconds
        }
    }
}

$summary | Format-Table -AutoSize
