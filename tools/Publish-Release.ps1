# Сборка релиза DriftHub + manifest.json для автообновлений.
# Использование:
#   .\tools\Publish-Release.ps1 -Version "1.0.1" [-Repo "Egzotik/drifthub"] [-SpotsDir "spots"] [-SkipBuild]
# Что делает:
#   1. Публикует single-file DriftHub.exe в publish-next (если не -SkipBuild).
#   2. Сканирует SpotsDir (папки с spot.json + *.ymap), считает SHA256.
#   3. Кладет в .feed/: DriftHub.exe, <id>.ymap, <id>.preview.<ext>, manifest.json
#   4. Если есть `gh` — создаёт GitHub-релиз v<Version> и заливает ассеты.
#      Если нет — печатает ручную инструкцию.
param(
    [string]$Version = "1.1.1",
    [string]$Repo = "Egzotik/drifthub",
    [string]$SpotsDir = "spots",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $Root

function Sha256([string]$path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
}

if (-not $SkipBuild) {
    Write-Host "== build publish-next =="
    dotnet publish "DriftHub.csproj" -c Release -r win-x64 --self-contained true `
        -o "publish-next" /p:PublishSingleFile=true /p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}

$Exe = Join-Path $Root "publish-next\DriftHub.exe"
if (-not (Test-Path -LiteralPath $Exe)) { throw "Нет $Exe — сначала собери без -SkipBuild" }
$ExeSha = Sha256 $Exe
Write-Host "exe sha256: $ExeSha"

$Feed = Join-Path $Root ".feed"
New-Item -ItemType Directory -Force -Path $Feed | Out-Null
Copy-Item -LiteralPath $Exe -Destination (Join-Path $Feed "DriftHub.exe") -Force

$Spots = @()
$Src = Join-Path $Root $SpotsDir
if (Test-Path -LiteralPath $Src) {
    foreach ($dir in Get-ChildItem -LiteralPath $Src -Directory | Where-Object { $_.Name -ne "_example" } | Sort-Object Name) {
        $jp = Join-Path $dir.FullName "spot.json"
        $ym = Get-ChildItem -LiteralPath $dir.FullName -Filter "*.ymap" -File | Select-Object -First 1
        if ($null -eq $ym) { Write-Host "skip $($dir.Name): нет .ymap"; continue }
        $meta = @{ id = $dir.Name; name = $dir.Name; description = ""; version = 1; ymap = $ym.Name }
        if (Test-Path -LiteralPath $jp) {
            try { $j = Get-Content -LiteralPath $jp -Raw | ConvertFrom-Json
                if ($j.id) { $meta.id = $j.id }
                if ($j.name) { $meta.name = $j.name }
                if ($j.description) { $meta.description = $j.description }
                if ($j.version) { $meta.version = [int]$j.version }
            } catch { Write-Host "warn: битый spot.json в $($dir.Name)" }
        }
        $id = $meta.id
        $ymapAsset = "$id.ymap"
        Copy-Item -LiteralPath $ym.FullName -Destination (Join-Path $Feed $ymapAsset) -Force
        $prev = Get-ChildItem -LiteralPath $dir.FullName -File | Where-Object { $_.Name -like "preview*" } | Select-Object -First 1
        $prevAsset = $null; $prevSha = $null
        if ($null -ne $prev) {
            $prevAsset = "$id.preview$($prev.Extension)"
            Copy-Item -LiteralPath $prev.FullName -Destination (Join-Path $Feed $prevAsset) -Force
            $prevSha = Sha256 (Join-Path $Feed $prevAsset)
        }
        $base = "https://github.com/$Repo/releases/download/v$Version"
        $entry = [ordered]@{
            id = $id; version = $meta.version; name = $meta.name; description = $meta.description
            file = $ymapAsset; ymapUrl = "$base/$ymapAsset"; ymapSha256 = (Sha256 (Join-Path $Feed $ymapAsset))
        }
        if ($prevAsset) {
            $entry.previewUrl = "$base/$prevAsset"
            $entry.previewSha256 = $prevSha
        }
        $Spots += $entry
        Write-Host "spot: $id v$($meta.version)"
    }
} else { Write-Host "warn: нет папки $SpotsDir — релиз только с exe" }

$manifest = [ordered]@{
    appVersion = $Version
    appUrl = "https://github.com/$Repo/releases/download/v$Version/DriftHub.exe"
    appSha256 = $ExeSha
    spots = $Spots
}
[IO.File]::WriteAllText((Join-Path $Feed "manifest.json"), ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
Write-Host "manifest: $($Spots.Count) спотов -> .feed\manifest.json"

$assets = @((Join-Path $Feed "DriftHub.exe"), (Join-Path $Feed "manifest.json")) +
    (Get-ChildItem -LiteralPath $Feed -File | Where-Object { $_.Name -notin @("DriftHub.exe","manifest.json") } | ForEach-Object { $_.FullName }))

if (Get-Command gh -ErrorAction SilentlyContinue) {
    Write-Host "== gh release create v$Version =="
    gh release create "v$Version" --repo $Repo --title "v$Version" --notes "DriftHub $Version" -- $assets
    Write-Host "OK: релиз залит. Проверка: https://github.com/$Repo/releases/latest/download/manifest.json"
} else {
    Write-Host ""
    Write-Host "gh не найден. Создай релиз вручную:"
    Write-Host "  1. https://github.com/$Repo/releases/new -> tag v$Version"
    Write-Host "  2. Приложи файлы из .feed/ :"
    foreach ($a in $assets) { Write-Host "     - $a" }
    Write-Host "  3. Проверь: https://github.com/$Repo/releases/latest/download/manifest.json"
}
