# Собрать границу ядра в DLL для клиента Windows и положить её туда, где её
# найдёт сборка .NET.
#
#   pwsh core/Scripts/build-windows.ps1
#
# Результат:
#   core/build/method_core_ffi.dll        — библиотека (x86_64)
#   core/build/include/method_core.h      — заголовок, копия из core/include
#
# Почему предсказуемый каталог, а не target/: путь target/<цель>/release/
# зависит от того, названа ли цель явно, и меняется при добавлении второй
# архитектуры. Файл проекта .NET правится руками, поэтому путь в нём обязан
# быть постоянным — build/ и есть этот постоянный путь.
#
# Скрипт ничего не устанавливает. Нет cargo или компоновщика MSVC — он честно
# говорит об этом, а не пытается доставить полтора гигабайта за спиной у
# человека.

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$out = Join-Path $root 'build'

$target = 'x86_64-pc-windows-msvc'
$lib = 'method_core_ffi.dll'

function Size([string]$path) {
    $bytes = (Get-Item $path).Length
    '{0:N1} МБ' -f ($bytes / 1MB)
}

# ── Проверки перед работой ──

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    if (Test-Path "$env:USERPROFILE\.cargo\bin\cargo.exe") {
        $env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
    }
    else {
        Write-Error 'cargo не найден. Установите Rust: winget install Rustlang.Rustup'
    }
}

# Компоновщик MSVC живёт не в PATH, а внутри установки Visual Studio; cargo
# находит его сам через vswhere. Проверяем именно наличие установки, потому что
# без неё отказ выглядит как невнятная ошибка компоновки в середине сборки.
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"

if (-not (Test-Path $vswhere)) {
    Write-Error @'
Не найден компоновщик MSVC. Установите средства сборки:
  winget install --id Microsoft.VisualStudio.2022.BuildTools -e --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
'@
}

# ── Сборка ──

# В PATH может лежать `link` из состава Git for Windows — это утилита coreutils
# для жёстких ссылок, а не компоновщик. Cargo зовёт компоновщик по имени
# `link.exe`, находит чужой и получает от него «extra operand» посреди сборки
# зависимостей. Сообщение не упоминает ни Git, ни PATH, поэтому чинится оно
# долго. Убираем каталоги Git из PATH на время сборки.
$env:PATH = ($env:PATH -split ';' | Where-Object {
    $_ -and $_ -notmatch '\\Git\\usr\\bin' -and $_ -notmatch '\\Git\\bin' -and $_ -notmatch '\\Git\\mingw'
}) -join ';'

Push-Location $root

try {
    Write-Host "Собираю границу ядра ($target)…"
    & cargo build --release --target $target -p method-core-ffi

    if ($LASTEXITCODE -ne 0) { Write-Error "cargo вернул $LASTEXITCODE" }
}
finally {
    Pop-Location
}

# ── Раскладка ──

$built = Join-Path $root "target\$target\release\$lib"
if (-not (Test-Path $built)) { Write-Error "сборка прошла, а $lib нет: $built" }

New-Item -ItemType Directory -Force -Path (Join-Path $out 'include') | Out-Null

Copy-Item $built (Join-Path $out $lib) -Force
Copy-Item (Join-Path $root 'include\method_core.h') (Join-Path $out 'include\method_core.h') -Force

Write-Host ("Готово: {0} ({1})" -f (Join-Path $out $lib), (Size (Join-Path $out $lib)))
