# Cài các thành phần cần cho Faceless Studio trên Windows.
# Chạy trong PowerShell từ thư mục gốc repo:
#   powershell -ExecutionPolicy Bypass -File scripts\setup-windows.ps1
# Tuỳ chọn: -SkipPiper để bỏ qua Piper TTS.

param([switch]$SkipPiper)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$data = Join-Path $root "data"
New-Item -ItemType Directory -Force -Path $data | Out-Null

function Has($cmd) { return [bool](Get-Command $cmd -ErrorAction SilentlyContinue) }

# 1. .NET 11 SDK (RC2)
$sdks = if (Has "dotnet") { dotnet --list-sdks } else { "" }
if ($sdks -notmatch "^11\.0\.") {
    Write-Host ">> Cài .NET 11 SDK (preview/RC)..." -ForegroundColor Cyan
    winget install --id Microsoft.DotNet.SDK.Preview -e --accept-source-agreements --accept-package-agreements
} else {
    Write-Host "OK  .NET 11 SDK đã có" -ForegroundColor Green
}

# 2. FFmpeg
if (-not (Has "ffmpeg")) {
    Write-Host ">> Cài FFmpeg..." -ForegroundColor Cyan
    winget install --id Gyan.FFmpeg -e --accept-source-agreements --accept-package-agreements
    Write-Host "   Mở lại cửa sổ PowerShell sau khi cài để PATH có ffmpeg." -ForegroundColor Yellow
} else {
    Write-Host "OK  FFmpeg đã có" -ForegroundColor Green
}

# 3. Piper TTS + giọng tiếng Việt, tiếng Anh
$settingsFile = Join-Path $data "settings.json"
$settings = @{}
if (Test-Path $settingsFile) {
    (Get-Content $settingsFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $settings[$_.Name] = $_.Value }
}

if (-not $SkipPiper) {
    $piperDir = Join-Path $data "piper"
    New-Item -ItemType Directory -Force -Path $piperDir | Out-Null
    $piperExe = Join-Path $piperDir "piper\piper.exe"
    if (-not (Test-Path $piperExe)) {
        Write-Host ">> Tải Piper..." -ForegroundColor Cyan
        $zip = Join-Path $env:TEMP "piper_windows_amd64.zip"
        Invoke-WebRequest "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip" -OutFile $zip
        Expand-Archive $zip -DestinationPath $piperDir -Force
    }
    $voices = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0"
    foreach ($v in @("vi/vi_VN/vais1000/medium/vi_VN-vais1000-medium", "en/en_US/lessac/medium/en_US-lessac-medium")) {
        $name = Split-Path $v -Leaf
        foreach ($ext in @(".onnx", ".onnx.json")) {
            $target = Join-Path $piperDir "$name$ext"
            if (-not (Test-Path $target)) {
                Write-Host ">> Tải giọng $name$ext..." -ForegroundColor Cyan
                Invoke-WebRequest "$voices/$v$ext" -OutFile $target
            }
        }
    }
    $settings["PIPER_PATH"] = $piperExe
    $settings["PIPER_VOICE_VI"] = Join-Path $piperDir "vi_VN-vais1000-medium.onnx"
    $settings["PIPER_VOICE_EN"] = Join-Path $piperDir "en_US-lessac-medium.onnx"
    $settings | ConvertTo-Json | Set-Content -Encoding UTF8 $settingsFile
    Write-Host "OK  Piper sẵn sàng, đã ghi đường dẫn vào data\settings.json" -ForegroundColor Green
}

New-Item -ItemType Directory -Force -Path (Join-Path $root "assets\music") | Out-Null

Write-Host ""
Write-Host "Xong! Chạy ứng dụng bằng:" -ForegroundColor Green
Write-Host "   dotnet run --project src\FacelessStudio.Web" -ForegroundColor White
Write-Host "rồi mở http://localhost:5080" -ForegroundColor White
Write-Host "Tuỳ chọn: cài Ollama (https://ollama.com) rồi chạy 'ollama pull qwen2.5:7b' để có LLM miễn phí trên máy."
