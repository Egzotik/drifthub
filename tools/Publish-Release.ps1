# DriftHub: споты + релизы.
#
# Споты для всех (без нового exe):
#   1. Положи спот в spots/<имя>/ (spot.json + .ymap + preview.jpg), подними version в spot.json.
#   2. .\tools\Publish-Release.ps1 -SpotsOnly
#   3. git add spots feed && git commit && git push  -> у пользователей появится само.
#
# Новый exe:
#   .\tools\Publish-Release.ps1 -Version "1.2.0" [-Repo "Egzotik/drifthub"]
#   Собирает exe в publish/, обновляет app-секцию feed/manifest.json,
#   заливает DriftHub.exe в GitHub-релиз через `gh` (или печатает ручную инструкцию).
#   Затем: git add feed && git commit && git push.
param(
    [string]$Version = "1.1.1",
    [string]$Repo = "Egzotik/drifthub",
    [string]$Branch = "main",
    [string]$SpotsDir = "spots",
    [switch]$SpotsOnly,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $Root

function Sha256([string]$path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
}

# --- споты: всегда из гита (raw URLs), хэши с локальных файлов ---
$Spots = @()
$Src = Join-Path $Root $SpotsDir
$rawBase = "https://raw.githubusercontent.com/$Repo/$Branch/$SpotsDir"
if (Test-Path -LiteralPath $Src) {
    foreach ($dir in Get-ChildItem -LiteralPath $Src -Directory | Where-Object { $_.Name -ne "_example" } | Sort-Object Name) {
        $jp = Join-Path $dir.FullName "spot.json"
        $ym = Get-ChildItem -LiteralPath $dir.FullName -Filter "*.ymap" -File | Select-Object -First 1
        if ($null -eq $ym) { Write-Host "skip $($dir.Name): нет .ymap"; continue }
        $meta = @{ id = $dir.Name; name = $dir.Name; description = ""; version = 1 }
        if (Test-Path -LiteralPath $jp) {
            try { $j = Get-Content -LiteralPath $jp -Raw | ConvertFrom-Json
                if ($j.id) { $meta.id = $j.id }
                if ($j.name) { $meta.name = $j.name }
                if ($j.description) { $meta.description = $j.description }
                if ($j.version) { $meta.version = [int]$j.version }
            } catch { Write-Host "warn: битый spot.json в $($dir.Name)"; continue }
        }
        $id = $meta.id
        $entry = [ordered]@{
            id = $id; version = $meta.version; name = $meta.name; description = $meta.description
            file = $ym.Name
            ymapUrl = "$rawBase/$($dir.Name)/$($ym.Name)"
            ymapSha256 = (Sha256 $ym.FullName)
        }
        $prev = Get-ChildItem -LiteralPath $dir.FullName -File | Where-Object { $_.Name -like "preview*" } | Select-Object -First 1
        if ($null -ne $prev) {
            $entry.previewUrl = "$rawBase/$($dir.Name)/$($prev.Name)"
            $entry.previewSha256 = (Sha256 $prev.FullName)
        }
        $Spots += $entry
        Write-Host "spot: $id v$($meta.version)"
    }
} else { Write-Host "warn: нет папки $SpotsDir" }

$FeedDir = Join-Path $Root "feed"
New-Item -ItemType Directory -Force -Path $FeedDir | Out-Null
$ManifestPath = Join-Path $FeedDir "manifest.json"

# app-секция: из существующего манифеста, либо новая при релизе exe
$appVersion = ""; $appUrl = ""; $appSha = ""
if (Test-Path -LiteralPath $ManifestPath) {
    try { $old = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
        $appVersion = $old.appVersion; $appUrl = $old.appUrl; $appSha = $old.appSha256
    } catch { Write-Host "warn: битый старый manifest, app-секция сброшена" }
}

if (-not $SpotsOnly) {
    Write-Host "== build publish =="
    try {
        dotnet publish "DriftHub.csproj" -c Release -r win-x64 --self-contained true `
            -o "publish" /p:PublishSingleFile=true /p:Version=$Version
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    } catch {
        throw "Не собралось (возможно, запущен publish\DriftHub.exe — закрой приложение и повтори). $($_.Exception.Message)"
    }
    $Exe = Join-Path $Root "publish\DriftHub.exe"
    $appVersion = $Version
    $appUrl = "https://github.com/$Repo/releases/download/v$Version/DriftHub.exe"
    $appSha = Sha256 $Exe
    Write-Host "exe sha256: $appSha"
}

$manifest = [ordered]@{
    appVersion = $appVersion
    appUrl = $appUrl
    appSha256 = $appSha
    spots = $Spots
}
[IO.File]::WriteAllText($ManifestPath, ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
Write-Host "manifest: $($Spots.Count) спотов -> feed\manifest.json"

if ($SpotsOnly) {
    Write-Host ""
    Write-Host "Дальше: git add spots feed; git commit -m 'spots: ...'; git push"
    Write-Host "Фид: https://raw.githubusercontent.com/$Repo/$Branch/feed/manifest.json"
    return
}

if (Get-Command gh -ErrorAction SilentlyContinue) {
    Write-Host "== gh release create v$Version =="
    gh release create "v$Version" --repo $Repo --title "v$Version" --notes "DriftHub $Version" -- (Join-Path $Root "publish\DriftHub.exe")
    Write-Host "Дальше: git add feed; git commit -m 'release v$Version'; git push"
} else {
    Write-Host ""
    Write-Host "gh не найден. Создай релиз вручную:"
    Write-Host "  1. https://github.com/$Repo/releases/new -> tag v$Version"
    Write-Host "  2. Приложи publish\DriftHub.exe"
    Write-Host "  3. git add feed; git commit -m 'release v$Version'; git push"
}
