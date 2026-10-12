#requires -Version 7
<#
.SYNOPSIS
アプリのアイコン（複数サイズ入りの ICO）と、バージョン情報に出す画像を、元の画像から作る。

.DESCRIPTION
artifacts/img/splkn_600.png（リポジトリには含めていない）を縮小して、16〜64 ピクセルの各サイズ（無圧縮）と 256 ピクセル（PNG）を 1 つの ICO にまとめ、
src/SoundPlayerChiyomaru/app.ico に書く。あわせて、設定画面のバージョン情報に出す 192 ピクセルの PNG を
src/SoundPlayerChiyomaru/about.png に書く（96 ピクセルで表示するので、拡大率 200% まで縮小だけで済む大きさ）。
元の画像を差し替えたときに実行し直す。

小さいサイズを無圧縮で入れておくのは、起動時間のため。256 ピクセルの PNG だけの ICO だと、Windows がウィンドウを作るときに
それを展開して縮小するので、ウィンドウの表示までが約 4ms 延びた（SPEC 9 章）。

.EXAMPLE
./tools/Make-AppIcon.ps1
#>
param(
    [string] $Source = (Join-Path $PSScriptRoot '..\artifacts\img\splkn_600.png'),
    [string] $Destination = (Join-Path $PSScriptRoot '..\src\SoundPlayerChiyomaru\app.ico'),
    [int[]] $Sizes = @(16, 20, 24, 32, 40, 48, 64, 256),
    [string] $AboutDestination = (Join-Path $PSScriptRoot '..\src\SoundPlayerChiyomaru\about.png'),
    [int] $AboutSize = 192
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Resize([System.Drawing.Image] $image, [int] $size) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
    $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
    $graphics.DrawImage($image, [System.Drawing.Rectangle]::new(0, 0, $size, $size), 0, 0, $image.Width, $image.Height, [System.Drawing.GraphicsUnit]::Pixel, $attributes)
    $graphics.Dispose()
    $bitmap
}

# 256 ピクセル未満は、ヘッダー・画素（下の行から、BGRA）・透過マスク（すべて 0。透過は画素のアルファで表す）の並び。
function ToIconBitmap([System.Drawing.Bitmap] $bitmap) {
    $size = $bitmap.Width
    $stream = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($stream)
    $writer.Write([int] 40); $writer.Write([int] $size); $writer.Write([int] ($size * 2))
    $writer.Write([int16] 1); $writer.Write([int16] 32); $writer.Write([int] 0); $writer.Write([int] ($size * $size * 4))
    $writer.Write([int] 0); $writer.Write([int] 0); $writer.Write([int] 0); $writer.Write([int] 0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $pixel = $bitmap.GetPixel($x, $y)
            $writer.Write([byte] $pixel.B); $writer.Write([byte] $pixel.G); $writer.Write([byte] $pixel.R); $writer.Write([byte] $pixel.A)
        }
    }
    $maskRow = [int] ([math]::Ceiling($size / 32.0) * 4)
    $writer.Write([byte[]]::new($maskRow * $size))
    $writer.Flush()
    , $stream.ToArray()
}

$image = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $Source).Path)
try {
    $entries = foreach ($size in $Sizes) {
        $bitmap = Resize $image $size
        try {
            if ($size -ge 256) {
                $stream = [System.IO.MemoryStream]::new()
                $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
                $data = $stream.ToArray()
            }
            else {
                $data = ToIconBitmap $bitmap
            }
            [pscustomobject]@{ Size = $size; Data = [byte[]] $data }
        }
        finally {
            $bitmap.Dispose()
        }
    }

    $about = Resize $image $AboutSize
    try {
        $aboutTarget = [System.IO.Path]::GetFullPath($AboutDestination)
        $about.Save($aboutTarget, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $about.Dispose()
    }
}
finally {
    $image.Dispose()
}

$output = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($output)
$writer.Write([int16] 0); $writer.Write([int16] 1); $writer.Write([int16] $entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($entry in $entries) {
    # 幅と高さは 1 バイトで、256 は 0 と書く。
    $dimension = [byte] ($entry.Size -ge 256 ? 0 : $entry.Size)
    $writer.Write($dimension); $writer.Write($dimension); $writer.Write([byte] 0); $writer.Write([byte] 0)
    $writer.Write([int16] 1); $writer.Write([int16] 32); $writer.Write([int] $entry.Data.Length); $writer.Write([int] $offset)
    $offset += $entry.Data.Length
}
foreach ($entry in $entries) {
    $writer.Write($entry.Data)
}
$writer.Flush()

$target = [System.IO.Path]::GetFullPath($Destination)
[System.IO.File]::WriteAllBytes($target, $output.ToArray())
"{0}（{1} バイト、{2} ピクセル）" -f $target, $output.Length, ($Sizes -join ' / ')
"{0}（{1} バイト、{2} ピクセル）" -f $aboutTarget, (Get-Item -LiteralPath $aboutTarget).Length, $AboutSize
