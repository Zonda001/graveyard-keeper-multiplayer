# build.ps1 — збірка + деплой мода
# Використання:
#   .\build.ps1           # просто зібрати
#   .\build.ps1 -Restart  # зібрати і перезапустити гру

param(
    [switch]$Restart,
    [string]$Config = "Debug"
)

$ErrorActionPreference = "Stop"
$proj = "$PSScriptRoot\Multiplayer\Multiplayer.csproj"
$game = "B:\SteamLibrary\steamapps\common\Graveyard Keeper\Graveyard Keeper.exe"
$log  = "B:\SteamLibrary\steamapps\common\Graveyard Keeper\BepInEx\LogOutput.log"

Write-Host "`n=== BUILD ($Config) ===" -ForegroundColor Cyan

$result = dotnet build $proj -c $Config --nologo 2>&1
$ok = $LASTEXITCODE -eq 0

$result | ForEach-Object {
    if ($_ -match "error")   { Write-Host $_ -ForegroundColor Red }
    elseif ($_ -match "warning") { Write-Host $_ -ForegroundColor Yellow }
    elseif ($_ -match "Deploy|succeeded") { Write-Host $_ -ForegroundColor Green }
    else { Write-Host $_ }
}

if (-not $ok) {
    Write-Host "`n✗ Build failed" -ForegroundColor Red
    exit 1
}

Write-Host "`n✓ Build succeeded + DLL deployed" -ForegroundColor Green

if ($Restart) {
    Write-Host "`n=== RESTART GAME ===" -ForegroundColor Cyan
    Stop-Process -Name "Graveyard Keeper" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Clear-Content $log -ErrorAction SilentlyContinue
    Start-Process "steam://rungameid/599140"
    Write-Host "✓ Гра запущена через Steam. Лог очищено." -ForegroundColor Green
    Write-Host "  Запусти .\log-watch.ps1 щоб слідкувати за логом`n" -ForegroundColor DarkGray
}
