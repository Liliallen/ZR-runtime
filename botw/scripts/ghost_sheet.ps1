# Side-by-side crops of dense captures from several runs (DLSS ghosting comparison), one row per run.
#   ghost_sheet.ps1 -Out sheet.png -Runs <dir>,<dir>,... [-From 20] [-Count 8] [-Step 3] [-Crop x,y,w,h] [-Scale 1]
# Each <dir> holds shots\*.png from bench_win.ps1 (sorted by name = time); frame i of every row is capture From+i*Step.
param(
    [Parameter(Mandatory)] [string]$Out,
    [Parameter(Mandatory)] [string[]]$Runs,
    [int]$From = 20, [int]$Count = 8, [int]$Step = 3,
    [int[]]$Crop = @(710, 380, 500, 400),
    [double]$Scale = 1
)
Add-Type -AssemblyName System.Drawing
$w = [int]($Crop[2] * $Scale); $h = [int]($Crop[3] * $Scale); $label = 22
$sheet = New-Object System.Drawing.Bitmap ($w * $Count), (($h + $label) * $Runs.Count)
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.Clear([System.Drawing.Color]::Black)
$font = New-Object System.Drawing.Font 'Arial', 12
$src = New-Object System.Drawing.Rectangle $Crop[0], $Crop[1], $Crop[2], $Crop[3]
for ($r = 0; $r -lt $Runs.Count; $r++) {
    $files = @(Get-ChildItem (Join-Path $Runs[$r] 'shots') -Filter *.png | Sort-Object Name)
    $y = $r * ($h + $label)
    $g.DrawString((Split-Path $Runs[$r] -Leaf), $font, [System.Drawing.Brushes]::Yellow, 4, $y + 2)
    for ($i = 0; $i -lt $Count; $i++) {
        $k = $From + $i * $Step
        if ($k -ge $files.Count) { break }
        $img = [System.Drawing.Image]::FromFile($files[$k].FullName)
        $g.DrawImage($img, (New-Object System.Drawing.Rectangle ($i * $w), ($y + $label), $w, $h), $src, [System.Drawing.GraphicsUnit]::Pixel)
        $img.Dispose()
        $g.DrawString("#$k", $font, [System.Drawing.Brushes]::White, ($i * $w + 200), $y + 2)
    }
}
$sheet.Save($Out)
"$Out ($($sheet.Width)x$($sheet.Height))"
