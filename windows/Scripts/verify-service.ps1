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
Приёмка привилегированной части: служба MethodTunnel.

.DESCRIPTION
Проверяет не то, что установка отработала без ошибок, а то, что получилось:
служба есть, поднимается сама, останавливается начисто и возвращается после
падения. Читать код и верить ему тут нельзя — всё это живёт в системе, а не в
нашей сборке.

Без ключа -Full проверки только читающие: ничего не останавливается и не
убивается. С ключом -Full служба будет остановлена и убита — туннель при этом
разорвётся, поэтому в разгар работы гонять не надо.

.PARAMETER Full
Выполнить и разрушающие проверки: корректная остановка и возврат после падения.

.PARAMETER Install
Перед проверками запустить установку: Method.exe --install.

.PARAMETER Exe
Путь к Method.exe. По умолчанию — установленный в Program Files.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File verify-service.ps1 -Full
#>
[CmdletBinding()]
param(
    [switch]$Full,
    [switch]$Install,
    [string]$Exe = (Join-Path $env:ProgramFiles 'Method\Method.exe')
)

# Объявление параметров обязано стоять раньше любого исполняемого кода, поэтому
# режим строгости включается только здесь.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Failures = 0
$script:Checks = 0

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return ([Security.Principal.WindowsPrincipal]$identity).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Ok($text) {
    $script:Checks++
    Write-Host "  [ да ] $text" -ForegroundColor Green
}

function Bad($text) {
    $script:Checks++
    $script:Failures++
    Write-Host "  [ НЕТ ] $text" -ForegroundColor Red
}

function Note($text) { Write-Host "  ---- $text" -ForegroundColor DarkGray }

function Assert($condition, $good, $bad) {
    if ($condition) { Ok $good } else { Bad $bad }
}

# Проба сети по числовому адресу. Имена не годятся: при поднятой блокировке не
# проходит и запрос имени, и «сети нет» стало бы неотличимо от «имя не
# разрешилось».
function Test-Internet {
    foreach ($address in @('1.1.1.1', '8.8.8.8', '9.9.9.9')) {
        try {
            $client = New-Object Net.Sockets.TcpClient
            $handle = $client.BeginConnect($address, 443, $null, $null)
            $done = $handle.AsyncWaitHandle.WaitOne(3000, $false)
            if ($done -and $client.Connected) { $client.Close(); return $true }
            $client.Close()
        } catch { }
    }
    return $false
}

# Канал привилегированной части. Существование — единственный признак, доступный
# снаружи: команды слать не будем, чтобы не трогать туннель.
function Test-Pipe {
    return [System.IO.Directory]::GetFiles('\\.\pipe\') -match 'MethodTunnelHost'
}

function Get-ServiceRecord {
    try { return Get-CimInstance Win32_Service -Filter "Name='MethodTunnel'" } catch { return $null }
}

if (-not (Test-Admin)) {
    Write-Host 'Нужны права администратора: служба живёт в системе, а не у пользователя.' -ForegroundColor Red
    exit 77
}

Write-Host 'Приёмка привилегированной части Method' -ForegroundColor White
Note "исполняемый файл: $Exe"

if ($Install) {
    Step 'Установка'

    if (-not (Test-Path $Exe)) {
        Bad "по пути $Exe файла нет — сначала опубликуйте сборку"
        exit 1
    }

    $process = Start-Process -FilePath $Exe -ArgumentList '--install' -Wait -PassThru -NoNewWindow
    Assert ($process.ExitCode -eq 0) 'установка завершилась без ошибки' `
        "установка вернула код $($process.ExitCode) — смотри $env:ProgramData\Method\install.log"
}

Step 'Запись службы'

$service = Get-ServiceRecord

if (-not $service) {
    Bad 'службы MethodTunnel в системе нет'
    Write-Host "`nПроверок: $script:Checks, из них не прошло: $script:Failures" -ForegroundColor Yellow
    exit 1
}

Ok 'служба MethodTunnel зарегистрирована'
Note "путь: $($service.PathName)"

Assert ($service.StartMode -eq 'Auto') 'запускается при загрузке' `
    "тип запуска $($service.StartMode) — при загрузке не поднимется"

Assert ($service.StartName -eq 'LocalSystem') 'работает под LocalSystem' `
    "работает под $($service.StartName) — прав на адаптер не хватит"

Assert ($service.State -eq 'Running') 'сейчас выполняется' `
    "состояние $($service.State) — привилегированной части нет"

# Путь обязан быть в кавычках: без них система читает «C:\Program» как имя файла.
Assert ($service.PathName -like '"*" --service') 'путь взят в кавычки и передан аргумент --service' `
    "путь записан как $($service.PathName) — пробел в пути разорвёт запуск"

Step 'Поведение при падении'

$failure = & sc.exe qfailure MethodTunnel 2>&1 | Out-String
Assert ($failure -match 'RESTART') 'перезапуск после падения настроен' `
    'действий при падении нет — упавшая служба останется лежать'

Step 'Следы прежних сборок'

$task = & schtasks.exe /Query /TN MethodTunnelHost 2>&1 | Out-String
Assert ($LASTEXITCODE -ne 0) 'задача планировщика снята' `
    'задача MethodTunnelHost всё ещё на месте — она поднимет вторую привилегированную часть'

Step 'Канал и журнал'

Assert (Test-Pipe) 'канал открыт — привилегированная часть отвечает' `
    'канала нет: служба выполняется, а слушать некому'

$sourceExists = $false
try { $sourceExists = [Diagnostics.EventLog]::SourceExists('Method') } catch { }
Assert $sourceExists 'источник «Method» в системном журнале зарегистрирован' `
    'источника в системном журнале нет — события пройдут мимо просмотра событий'

if (-not $Full) {
    Write-Host "`nЧитающие проверки пройдены. Разрушающие (остановка и падение) — ключ -Full." -ForegroundColor Yellow
    Write-Host "Проверок: $script:Checks, из них не прошло: $script:Failures" -ForegroundColor Yellow
    exit ([int]($script:Failures -gt 0))
}

Step 'Остановка начисто'

if (-not (Test-Internet)) {
    Bad 'сети нет ещё до остановки — отличить нашу блокировку от общего отсутствия сети будет нельзя'
} else {
    Stop-Service MethodTunnel -Force
    (Get-Service MethodTunnel).WaitForStatus('Stopped', '00:00:40')
    Ok 'служба остановилась по команде'

    Assert (Test-Internet) 'сеть после остановки на месте' `
        'СЕТИ НЕТ после остановки службы: блокировка пережила уход того, кто её ставил'

    # Имя с номером — «Method 2» и далее — Windows даёт сама, когда прежний
    # адаптер ещё не отпущен. Такой тоже наш, и остаться после нас не должен.
    $adapter = Get-NetAdapter -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq 'Method' -or $_.Name -match '^Method \d+$' }

    Assert ($null -eq $adapter) 'адаптер туннеля убран' `
        "адаптер остался после остановки: $($adapter.Name -join ', ')"

    $rules = & netsh.exe advfirewall firewall show rule name=all 2>&1 | Select-String 'Method' | Out-String
    if ($rules.Trim()) { Note "правила с упоминанием Method:`n$rules" }

    $engine = Get-Process sing-box -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -like "$env:ProgramData\Method\*" }
    Assert ($null -eq $engine) 'движок погашен вместе со службой' `
        'sing-box остался сиротой после остановки службы'

    Start-Service MethodTunnel
    (Get-Service MethodTunnel).WaitForStatus('Running', '00:00:40')
    Ok 'служба поднялась обратно'
}

Step 'Возврат после падения'

$record = Get-ServiceRecord
if ($record.ProcessId -gt 0) {
    Stop-Process -Id $record.ProcessId -Force
    Note "процесс $($record.ProcessId) убит, жду возврата до 60 секунд"

    $returned = $false
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 1
        if ((Get-Service MethodTunnel).Status -eq 'Running') { $returned = $true; break }
    }

    Assert $returned "система подняла службу заново (через $i с)" `
        'после падения служба не вернулась за минуту'
} else {
    Bad 'у службы нет процесса — падать нечему'
}

Step 'Что записано в системный журнал'

try {
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Method' } -MaxEvents 10 -ErrorAction Stop |
        ForEach-Object { Note "$($_.TimeCreated.ToString('HH:mm:ss')) [$($_.Id)] $($_.LevelDisplayName): $($_.Message)" }
} catch {
    Note 'записей от источника Method пока нет — это нормально, если туннель ни разу не поднимали'
}

Write-Host "`nПроверок: $script:Checks, из них не прошло: $script:Failures" -ForegroundColor Yellow
exit ([int]($script:Failures -gt 0))
