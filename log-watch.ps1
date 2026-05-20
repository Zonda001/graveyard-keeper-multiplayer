# log-watch.ps1 — live-перегляд BepInEx логу з підсвічуванням
# Використання: .\log-watch.ps1

$log = "B:\SteamLibrary\steamapps\common\Graveyard Keeper\BepInEx\LogOutput.log"

if (-not (Test-Path $log)) {
    Write-Host "Лог не знайдено: $log" -ForegroundColor Red
    exit 1
}

Write-Host "=== BepInEx Log Watch (Ctrl+C для виходу) ===" -ForegroundColor Cyan
Write-Host "Файл: $log`n" -ForegroundColor DarkGray

Get-Content $log -Wait -Tail 0 | ForEach-Object {
    $line = $_
    if     ($line -match "\[Fatal|Error\s*\]")   { Write-Host $line -ForegroundColor Red }
    elseif ($line -match "\[Warning\s*\]")         { Write-Host $line -ForegroundColor Yellow }
    elseif ($line -match "✓|succeeded|Готово")     { Write-Host $line -ForegroundColor Green }
    elseif ($line -match "\[STEAM\]")              { Write-Host $line -ForegroundColor Magenta }
    elseif ($line -match "\[SYNC\]|\[P2P\]")       { Write-Host $line -ForegroundColor Cyan }
    elseif ($line -match "\[REMOTE\]|\[CLIENT\]")  { Write-Host $line -ForegroundColor Blue }
    elseif ($line -match "\[SCENE\]")              { Write-Host $line -ForegroundColor DarkYellow }
    else                                           { Write-Host $line }
}
