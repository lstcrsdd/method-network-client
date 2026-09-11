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
Живая проверка интерфейса: запустить клиент и снять его окно.

.DESCRIPTION
Интерфейс нельзя принять по исходникам: разметка собирается в дерево уже на
машине, и половина огрехов — съехавшая колонка, вылезший за край текст, не
применившийся набор оформления — видна только глазами.

Скрипт запускает отладочную сборку, ставит окно на свободный экран, снимает его
и, если попросили, обходит разделы. Снимок берётся через PrintWindow с флагом
PW_RENDERFULLCONTENT: он копирует окно, не вытаскивая его на передний план, —
работать за машиной в это время можно.

Разделы переключаются через доступность (UI Automation), а не щелчком по
координатам: щелчок отбирает фокус и промахивается, стоит подписи переехать на
десяток пикселей.

Про два процесса Method.exe. В системе может работать привилегированная часть —
служба под SYSTEM. У неё нет окон, и командная строка у неё не читается: по
этому признаку скрипт и отличает её от своего отладочного клиента. Гасить службу
нельзя, туннель разорвётся.

.PARAMETER Sections
Какие разделы снять: Overview, Servers, Scenarios, Settings. По умолчанию тот,
что открыт.

.PARAMETER Frames
Снять серию кадров подряд — так ловят заставку запуска и другое движение.
Требует перезапуска клиента, поэтому включает -Restart.

.PARAMETER EveryMs
Пауза между кадрами серии. По умолчанию 250 мс.

.PARAMETER Build
Пересобрать решение перед запуском. Клиент при этом гасится: работающий
Method.exe держит свой файл, и сборка на него ругается.

.PARAMETER Restart
Погасить свой клиент и запустить заново, даже без пересборки.

.PARAMETER Screen
На каком экране показать окно: Primary или Secondary. По умолчанию Secondary —
на основном человек обычно работает.

.PARAMETER Out
Куда складывать снимки. По умолчанию каталог, откуда запущен скрипт.

.PARAMETER Name
Имя снимков. К нему добавляется раздел или номер кадра.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File preview.ps1 -Build -Sections Overview,Settings

.EXAMPLE
powershell -ExecutionPolicy Bypass -File preview.ps1 -Frames 8 -EveryMs 300 -Name boot
#>

[CmdletBinding()]
param(
    [ValidateSet('Overview', 'Servers', 'Scenarios', 'Settings')]
    [string[]] $Sections,

    [int] $Frames = 0,
    [int] $EveryMs = 250,

    [switch] $Build,
    [switch] $Restart,

    [ValidateSet('Primary', 'Secondary')]
    [string] $Screen = 'Secondary',

    [string] $Out = $PSScriptRoot,
    [string] $Name = 'shot',

    [int] $Width = 1180,
    [int] $Height = 780
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes

if (-not ('MethodPreview.Win' -as [type])) {
    Add-Type -Namespace MethodPreview -Name Win -UsingNamespace System.Drawing -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
[DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int h2, bool repaint);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT rect);
public struct RECT { public int Left, Top, Right, Bottom; }
'@
}

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'Method.App\bin\Debug\net8.0-windows\win-x64\Method.exe'

function Get-OwnClient {
    # Своя отладочная сборка: командная строка читается и в ней нет режима.
    # У службы (--service) командная строка недоступна вовсе.
    Get-CimInstance Win32_Process -Filter "Name='Method.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine -notmatch '--' } |
        Select-Object -First 1
}

function Stop-OwnClient {
    $mine = Get-OwnClient
    if (-not $mine) { return }

    Stop-Process -Id $mine.ProcessId -Force
    Start-Sleep -Milliseconds 500
}

function Build-Client {
    Push-Location $root
    try {
        $log = dotnet build Method.sln -c Debug -v q --nologo 2>&1
        $bad = $log | Select-String -Pattern ': error '
        if ($bad) {
            $bad | Select-Object -First 8 | ForEach-Object { Write-Host $_ }
            throw 'сборка не прошла'
        }
    }
    finally { Pop-Location }
}

function Start-Client {
    if (-not (Test-Path $exe)) { throw "нет отладочной сборки: $exe" }

    $process = Start-Process $exe -PassThru
    for ($i = 0; $i -lt 200 -and $process.MainWindowHandle -eq 0; $i++) {
        Start-Sleep -Milliseconds 20
        $process.Refresh()
    }

    if ($process.MainWindowHandle -eq 0) { throw 'окно не появилось' }
    return $process
}

function Move-ToScreen([IntPtr] $handle) {
    $target = if ($Screen -eq 'Primary') {
        [System.Windows.Forms.Screen]::AllScreens | Where-Object { $_.Primary }
    }
    else {
        [System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary }
    }

    # Второго экрана может не быть — тогда некуда убирать, показываем как есть.
    if (-not $target) { $target = [System.Windows.Forms.Screen]::PrimaryScreen }

    [MethodPreview.Win]::MoveWindow(
        $handle, $target.Bounds.X + 30, $target.Bounds.Y + 30, $Width, $Height, $true) | Out-Null
    Start-Sleep -Milliseconds 400
}

function Open-Section([IntPtr] $handle, [string] $section) {
    $titles = @{
        Overview  = 'Обзор'
        Servers   = 'Серверы'
        Scenarios = 'Сценарии'
        Settings  = 'Настройки'
    }

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    $element = $null

    # Плакатное оформление пишет названия прописными, основное — как есть.
    foreach ($title in @($titles[$section], $titles[$section].ToUpperInvariant())) {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $title)
        $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($element) { break }
    }

    if (-not $element) { Write-Warning "не нашёл раздел $section"; return }

    # Имя висит на надписи внутри кнопки — поднимаемся до того, кто умеет Invoke.
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $node = $element
    while ($node) {
        $pattern = $null
        if ($node.TryGetCurrentPattern(
                [System.Windows.Automation.InvokePattern]::Pattern, [ref] $pattern)) {
            $pattern.Invoke()
            Start-Sleep -Milliseconds 800
            return
        }
        $node = $walker.GetParent($node)
    }
}

function Save-Shot([IntPtr] $handle, [string] $path) {
    $rect = New-Object MethodPreview.Win+RECT
    [MethodPreview.Win]::GetWindowRect($handle, [ref] $rect) | Out-Null

    $bitmap = New-Object System.Drawing.Bitmap (
        ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()

    # 2 = PW_RENDERFULLCONTENT: снимает и то, что рисует видеокарта, и делает это
    # без вывода окна на передний план.
    [MethodPreview.Win]::PrintWindow($handle, $dc, 2) | Out-Null

    $graphics.ReleaseHdc($dc)
    $graphics.Dispose()
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()

    Write-Host "снято: $path"
}

if ($Build) { Stop-OwnClient; Build-Client }
elseif ($Restart -or $Frames -gt 0) { Stop-OwnClient }

$mine = Get-OwnClient
$client = if ($mine) { Get-Process -Id $mine.ProcessId } else { Start-Client }
$client.Refresh()

$handle = $client.MainWindowHandle
if ($handle -eq 0) { throw 'окно не появилось' }

New-Item -ItemType Directory -Force -Path $Out | Out-Null
Move-ToScreen $handle

if ($Frames -gt 0) {
    for ($n = 1; $n -le $Frames; $n++) {
        Save-Shot $handle (Join-Path $Out "$Name-$n.png")
        Start-Sleep -Milliseconds $EveryMs
    }
    return
}

if (-not $Sections) {
    Save-Shot $handle (Join-Path $Out "$Name.png")
    return
}

foreach ($section in $Sections) {
    Open-Section $handle $section
    Save-Shot $handle (Join-Path $Out "$Name-$($section.ToLower()).png")
}
