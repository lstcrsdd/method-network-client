# Copyright (C) 2026  Kovalyov Georgiy
# This file is part of Method.
#
# Method is free software: you can redistribute it and/or modify it under the
# terms of the GNU General Public License as published by the Free Software
# Foundation, either version 3 of the License, or (at your option) any later
# version. See LICENSE for details.

#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Приносит движок в ..\Resources. В репозитории его нет: sing-box живёт на своих
# условиях и распространяется отдельно, а wintun.dll — сетевой драйвер, которому
# в чужом репозитории делать нечего.
#
# Версия sing-box держится равной той, что берёт macos/Scripts/bootstrap.sh.
# Разъехавшиеся версии дают разное поведение туннеля на разных платформах, и
# искать причину потом будут в клиенте.

$SbVersion     = '1.13.13'
$WintunVersion = '0.14.1'

# Контрольные суммы распакованных файлов, а не архивов: проверяется ровно то, что
# ляжет рядом с клиентом. Снимаются командой
#     Get-FileHash -Algorithm SHA256 <файл>
# и при подъёме версии обновляются вручную — несовпадение обязано ронять сборку,
# а не печатать предупреждение, которое никто не прочтёт.
$SbSha256     = 'AF59A77A8171DD74B0B560CE34439A85B44CB82D87ACFD8B6D2C4C933AD0397D'
$WintunSha256 = 'E5DA8447DC2C320EDC0FC52FA01885C103DE8C118481F683643CACC3220DAFCE'

$Root   = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Res    = Join-Path $Root 'Resources'
$Cache  = Join-Path $Root '.tools'

New-Item -ItemType Directory -Force -Path $Res, $Cache | Out-Null

function Say  ([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Fail ([string]$m) { throw $m }

function Test-Sha256 {
    param([string]$Path, [string]$Expected)
    if (-not (Test-Path $Path)) { return $false }
    (Get-FileHash -Algorithm SHA256 -Path $Path).Hash -eq $Expected
}

# Скачивает архив, достаёт из него один файл и сверяет контрольную сумму.
# Файл кладётся на место только после сверки: наполовину записанный движок
# выглядит установленным и падает уже при запуске туннеля.
function Get-Component {
    param(
        [string]$Name,
        [string]$Url,
        [string]$EntryPattern,
        [string]$Target,
        [string]$Sha256,
        [string]$PreferPathMatch
    )

    if (Test-Sha256 -Path $Target -Expected $Sha256) {
        Say "$Name — на месте"
        return
    }
    if (Test-Path $Target) {
        Say "$Name — файл на месте, но сумма не та; забираю заново"
    }

    Say "Скачиваю $Name…"
    $zip = Join-Path $Cache ([IO.Path]::GetFileName($Url))
    Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing

    $tmp = Join-Path $Cache ("extract-" + [IO.Path]::GetFileNameWithoutExtension($Url))
    if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
    Expand-Archive -Path $zip -DestinationPath $tmp -Force

    $found = Get-ChildItem -Path $tmp -Recurse -Filter $EntryPattern
    if ($PreferPathMatch) {
        $narrowed = $found | Where-Object { $_.FullName -match $PreferPathMatch }
        if ($narrowed) { $found = $narrowed }
    }
    $file = $found | Select-Object -First 1
    if (-not $file) { Fail "$EntryPattern не найден в архиве $Url" }

    $actual = (Get-FileHash -Algorithm SHA256 -Path $file.FullName).Hash
    if ($actual -ne $Sha256) {
        Fail ("Контрольная сумма $Name не совпала.`n  ожидалась: $Sha256`n  получена:  $actual`n" +
              "  источник:  $Url`nФайл на место не положен.")
    }

    Copy-Item $file.FullName $Target -Force
    Remove-Item -Recurse -Force $tmp
    Say "$Name — проверен и на месте"
}

Get-Component -Name "sing-box $SbVersion" `
    -Url "https://github.com/SagerNet/sing-box/releases/download/v$SbVersion/sing-box-$SbVersion-windows-amd64.zip" `
    -EntryPattern 'sing-box.exe' `
    -Target (Join-Path $Res 'sing-box.exe') `
    -Sha256 $SbSha256

# В архиве Wintun лежат сборки под все архитектуры; нужна amd64.
Get-Component -Name "Wintun $WintunVersion" `
    -Url "https://www.wintun.net/builds/wintun-$WintunVersion.zip" `
    -EntryPattern 'wintun.dll' `
    -Target (Join-Path $Res 'wintun.dll') `
    -Sha256 $WintunSha256 `
    -PreferPathMatch 'amd64'

Say 'Готово. Дальше:'
Write-Host '    dotnet build Method.sln -c Debug'
