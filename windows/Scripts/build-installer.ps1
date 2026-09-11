# Copyright (C) 2026  Kovalyov Georgiy
# This file is part of Method.
#
# Method is free software: you can redistribute it and/or modify it under the
# terms of the GNU General Public License as published by the Free Software
# Foundation, either version 3 of the License, or (at your option) any later
# version. See LICENSE for details.

#Requires -Version 5.1

<#
.SYNOPSIS
Собрать установщик Windows-клиента.

.DESCRIPTION
Делает три вещи по порядку: публикует клиент одним самодостаточным файлом,
проверяет, что в нём есть всё нужное, и собирает установщик Inno Setup.

Проверки между шагами не формальность. Клиент технически собирается и без
движка, и без библиотеки ядра — просто в первом случае он не поднимет туннель,
а во втором выбор узла ведёт сам движок вместо ядра. Выпустить такую сборку
молча нельзя: узнают об этом уже на чужой машине.

.PARAMETER Version
Версия сборки. По умолчанию берётся из Directory.Build.props — там же, откуда
её берут сами проекты, чтобы номер в установщике и в свойствах файла не
разъезжались.

.PARAMETER SkipPublish
Не публиковать заново, взять готовое из publish\win-x64. Для правки самого
установщика, когда клиент уже собран.

.PARAMETER Output
Куда положить готовый установщик. По умолчанию Installer\Output.

.EXAMPLE
.\Scripts\build-installer.ps1

.EXAMPLE
.\Scripts\build-installer.ps1 -Version 0.2.0
#>
param(
    [string] $Version,
    [switch] $SkipPublish,
    [string] $Output
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot          # ...\windows
$repo = Split-Path -Parent $root                  # корень репозитория
$publish = Join-Path $root "publish\win-x64"
$iss = Join-Path $root "Installer\Method.iss"
if (-not $Output) { $Output = Join-Path $root "Installer\Output" }

function Step([string] $text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Fail([string] $text) { Write-Host "!!! $text" -ForegroundColor Red; exit 1 }

# Версию берём оттуда же, откуда её берут проекты: два места для одного числа
# расходятся, и расхождение видно только в свойствах готового файла.
if (-not $Version) {
    $props = Join-Path $root "Directory.Build.props"
    $match = Select-String -Path $props -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
    if (-not $match) { Fail "не нашёл <Version> в $props" }
    $Version = $match.Matches[0].Groups[1].Value
}

Step "версия $Version"

# Движок обязателен: без него клиент не поднимет туннель вовсе.
foreach ($file in @("sing-box.exe", "wintun.dll")) {
    if (-not (Test-Path (Join-Path $root "Resources\$file"))) {
        Fail "нет Resources\$file — запусти Scripts\bootstrap.ps1"
    }
}

# Библиотека ядра тоже обязательна для выпуска. Без неё клиент работает, но
# маршруты выбирает сам движок: без замеров, без объяснений и без памяти между
# запусками. Выпускать это под видом готового клиента нельзя.
$core = Join-Path $repo "core\build\method_core_ffi.dll"
if (-not (Test-Path $core)) {
    Fail "нет $core — собери ядро: core\Scripts\build-windows.ps1"
}

if (-not $SkipPublish) {
    Step "публикация клиента"

    # Клиент держит свои файлы, пока работает: публикация в занятый каталог
    # молча пропустит замену, и в установщик уедет прошлая сборка.
    Get-Process Method -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($root, 'OrdinalIgnoreCase') } |
        ForEach-Object { Write-Host "    гашу свой клиент PID $($_.Id)"; Stop-Process -Id $_.Id -Force }

    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    & dotnet publish (Join-Path $root "Method.App\Method.App.csproj") `
        -c Release -r win-x64 --self-contained true `
        -p:Version=$Version `
        -o $publish --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail "публикация не удалась" }
}

$exe = Join-Path $publish "Method.exe"
if (-not (Test-Path $exe)) { Fail "нет $exe" }

# Библиотеку ядра кладём рядом руками: в публикации она появляется только как
# побочный эффект копирования в выходной каталог, и полагаться на это нельзя.
Copy-Item $core (Join-Path $publish "method_core_ffi.dll") -Force

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Step "клиент собран: Method.exe, $size МБ"

# Версия внутри файла должна совпасть с той, что уйдёт в установщик: по ней
# мастер потом проверяет, что замена состоялась.
$fileVersion = (Get-Item $exe).VersionInfo.ProductVersion
if ($fileVersion -and -not $fileVersion.StartsWith($Version)) {
    Fail "в Method.exe версия $fileVersion, а собираем $Version"
}

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) { Fail "не нашёл ISCC.exe — поставь Inno Setup: winget install JRSoftware.InnoSetup" }

Step "сборка установщика"

& $iscc $iss "/DSource=$publish" "/DVersion=$Version" "/O$Output" | ForEach-Object {
    if ($_ -match 'error|Error') { Write-Host $_ -ForegroundColor Red } else { Write-Verbose $_ }
}
if ($LASTEXITCODE -ne 0) { Fail "Inno Setup вернул код $LASTEXITCODE" }

$setup = Join-Path $Output "Method-Setup-$Version.exe"
if (-not (Test-Path $setup)) { Fail "установщик не появился: $setup" }

$setupSize = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host ""
Write-Host "готово: $setup ($setupSize МБ)" -ForegroundColor Green
Write-Host "подписи нет — SmartScreen будет предупреждать при первом запуске." -ForegroundColor Yellow
