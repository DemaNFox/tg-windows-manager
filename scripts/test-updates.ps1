param(
    [switch]$TestTelegram,
    [int]$Port = 8765
)

$ErrorActionPreference = 'Stop'

function New-TempDir([string]$prefix) {
    $root = Join-Path $env:TEMP ($prefix + '-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root | Out-Null
    return $root
}

function Copy-Dir([string]$src, [string]$dst) {
    if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
    New-Item -ItemType Directory -Path $dst | Out-Null
    Copy-Item -Path (Join-Path $src '*') -Destination $dst -Recurse -Force
}

function New-ZipFromFolder([string]$source, [string]$zipPath) {
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($source, $zipPath)
}

function Start-SimpleHttpServer([string]$root, [int]$port) {
    $job = Start-Job -ScriptBlock {
        param($Root, $Port)
        Add-Type -AssemblyName System.Net.HttpListener
        $listener = New-Object System.Net.HttpListener
        $listener.Prefixes.Add("http://localhost:$Port/")
        $listener.Start()
        try {
            while ($listener.IsListening) {
                $context = $listener.GetContext()
                $request = $context.Request
                $response = $context.Response
                $path = $request.Url.AbsolutePath.TrimStart('/')
                if ([string]::IsNullOrWhiteSpace($path)) { $path = 'index.html' }
                $filePath = Join-Path $Root $path
                if (Test-Path $filePath) {
                    $bytes = [System.IO.File]::ReadAllBytes($filePath)
                    $response.ContentLength64 = $bytes.Length
                    $response.OutputStream.Write($bytes, 0, $bytes.Length)
                } else {
                    $response.StatusCode = 404
                }
                $response.OutputStream.Close()
            }
        } finally {
            $listener.Stop()
            $listener.Close()
        }
    } -ArgumentList $root, $port
    return $job
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishProfile = Join-Path $repoRoot 'Properties\PublishProfiles\Portable.pubxml'
if (!(Test-Path $publishProfile)) {
    throw "Publish profile not found: $publishProfile"
}

Write-Host "Publishing portable build..." -ForegroundColor Cyan
pushd $repoRoot
try {
    dotnet publish -p:PublishProfile=Portable | Out-Host
} finally {
    popd
}

$publishDir = Join-Path $repoRoot 'bin\Release\net8.0-windows\win-x64\publish'
if (!(Test-Path $publishDir)) {
    throw "Publish output not found: $publishDir"
}

$tempRoot = New-TempDir 'tg-update-test'
$currentDir = Join-Path $tempRoot 'current'
$updateDir = Join-Path $tempRoot 'update'

Copy-Dir $publishDir $currentDir
Copy-Dir $publishDir $updateDir

# Marker to verify update applied
$markerPath = Join-Path $updateDir 'update-marker.txt'
[System.IO.File]::WriteAllText($markerPath, "updated at $(Get-Date -Format s)")

$updateZip = Join-Path $tempRoot 'app-update.zip'
New-ZipFromFolder $updateDir $updateZip

$appUpdateJson = @{
    LocalPath = $updateZip
    TestMode = $false
} | ConvertTo-Json
[System.IO.File]::WriteAllText((Join-Path $currentDir 'app_update.json'), $appUpdateJson)

Write-Host "App update test ready." -ForegroundColor Green
Write-Host "Current dir: $currentDir"
Write-Host "Update zip: $updateZip"

if ($TestTelegram) {
    $serverRoot = Join-Path $tempRoot 'server'
    New-Item -ItemType Directory -Path $serverRoot | Out-Null

    # Create a fake Telegram archive containing Telegram/Telegram.exe
    $fakeTelegramDir = Join-Path $tempRoot 'fake-telegram'
    New-Item -ItemType Directory -Path (Join-Path $fakeTelegramDir 'Telegram') | Out-Null
    $fakeExe = Join-Path $fakeTelegramDir 'Telegram\Telegram.exe'
    [System.IO.File]::WriteAllText($fakeExe, "fake telegram exe $(Get-Date -Format s)")

    $telegramZip = Join-Path $serverRoot 'telegram.zip'
    New-ZipFromFolder $fakeTelegramDir $telegramZip

    $versionFile = Join-Path $serverRoot 'version.txt'
    [System.IO.File]::WriteAllText($versionFile, '99.99.99')

    $job = Start-SimpleHttpServer $serverRoot $Port
    Write-Host "Local update server running on http://localhost:$Port/" -ForegroundColor Yellow

    $telegramUpdateJson = @{
        VersionUrl = "http://localhost:$Port/version.txt"
        DownloadUrl = "http://localhost:$Port/telegram.zip"
    } | ConvertTo-Json
    [System.IO.File]::WriteAllText((Join-Path $currentDir 'telegram_update.json'), $telegramUpdateJson)

    Write-Host "Telegram update test ready (fake archive)." -ForegroundColor Green
}

Write-Host "Launching test app..." -ForegroundColor Cyan
$prevAuto = $env:TG_UPDATE_AUTO_ACCEPT
$prevAllowSame = $env:TG_UPDATE_ALLOW_SAME_VERSION
$prevTest = $env:TG_UPDATE_TEST
$env:TG_UPDATE_AUTO_ACCEPT = '1'
$env:TG_UPDATE_ALLOW_SAME_VERSION = '1'
$env:TG_UPDATE_TEST = '1'
$process = Start-Process -FilePath (Join-Path $currentDir 'TelegramTrayLauncher.exe') -WorkingDirectory $currentDir -PassThru
$env:TG_UPDATE_AUTO_ACCEPT = $prevAuto
$env:TG_UPDATE_ALLOW_SAME_VERSION = $prevAllowSame
$env:TG_UPDATE_TEST = $prevTest
Write-Host "After update, verify marker exists:" -ForegroundColor Cyan
Write-Host "  $currentDir\update-marker.txt"
Write-Host "Logs: $currentDir\app_update.log and $currentDir\telegram_update.log"

Write-Host "Waiting for update marker..." -ForegroundColor Cyan
$deadline = (Get-Date).AddSeconds(120)
$markerPath = Join-Path $currentDir 'update-marker.txt'
while ((Get-Date) -lt $deadline -and !(Test-Path $markerPath)) {
    Start-Sleep -Milliseconds 500
}
if (Test-Path $markerPath) {
    Write-Host "Update marker found." -ForegroundColor Green
    Get-Content -Path $markerPath | Out-Host
} else {
    Write-Host "Update marker not found within timeout." -ForegroundColor Red
}

$appUpdatePath = Join-Path $currentDir 'app_update.json'
if (Test-Path $appUpdatePath) {
    try { Remove-Item -Path $appUpdatePath -Force } catch {}
}

Write-Host "Attempting to close test app..." -ForegroundColor Cyan
if ($process -and !$process.HasExited) {
    try { $process.CloseMainWindow() | Out-Null } catch {}
    Start-Sleep -Milliseconds 300
    try { if (!$process.HasExited) { Stop-Process -Id $process.Id -Force } } catch {}
}

if ($TestTelegram -and $job) {
    Stop-Job $job | Out-Null
    Remove-Job $job | Out-Null
}

