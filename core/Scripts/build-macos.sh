#!/bin/sh
# Собрать статическую библиотеку границы для приложения macOS и положить её
# туда, где Xcode найдёт её по постоянному пути.
#
#   sh Scripts/build-macos.sh
#
# Результат:
#   Core/build/libmethod_core_ffi.a          — статика (arm64, при наличии цели + x86_64)
#   Core/build/include/method_core.h         — заголовок, копия из Core/include
#
# Почему предсказуемый каталог, а не target/: путь target/<цель>/release/
# зависит от того, назвали ли цель явно, и меняется при добавлении второй
# архитектуры. Настройки Xcode правятся руками, поэтому путь в них обязан быть
# постоянным — build/ и есть этот постоянный путь.
#
# Скрипт ничего не устанавливает: цель x86_64-apple-darwin, если её нет,
# он не ставит, а честно предупреждает.
set -eu

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/.." && pwd)
out="$root/build"

ARM=aarch64-apple-darwin
INTEL=x86_64-apple-darwin
LIB=libmethod_core_ffi.a

mb() { awk -v b="$1" 'BEGIN{ printf "%.1f МБ", b/1048576 }'; }
kb() { awk -v b="$1" 'BEGIN{ printf "%.0f КБ", b/1024 }'; }
bytes() { stat -f%z "$1"; }

# ── 0. Инструменты ───────────────────────────────────────────────────────────
command -v cargo >/dev/null 2>&1 || { echo "нет cargo — поставь Rust: https://rustup.rs" >&2; exit 1; }

# Установлена ли цель под Intel. Спрашиваем rustup, а не пробуем собрать:
# неудачная сборка выглядит как поломка скрипта, а отсутствие цели — не
# поломка, а известное состояние машины.
have_intel=0
if command -v rustup >/dev/null 2>&1; then
    if rustup target list --installed 2>/dev/null | grep -qx "$INTEL"; then
        have_intel=1
    fi
fi

# ── 1. Сборка ────────────────────────────────────────────────────────────────
# --release без правки профиля: параметры оптимизации живут в Cargo.toml
# рабочего пространства (там же объяснено, почему panic = "unwind"). Скрипт
# намеренно НЕ подкручивает их через RUSTFLAGS — иначе то, что уедет к людям,
# отличалось бы от того, что собирает `cargo build --release` при отладке.
#
# Флаг test-panic НЕ включаем: mc_test_panic — тестовая функция, в продукте
# её быть не должно.
# Пути сборки НЕ должны уезжать к людям. Rust вшивает их в сообщения о
# панике внутри зависимостей, и в готовом приложении оказывались строки вида
# «/Users/имя/.cargo/registry/…» — секретов там нет, но имя пользователя
# машины сборки видно всем, кто откроет бинарь. Проверено на выпуске 1.0.0:
# восемь таких строк. Оптимизацию это не трогает, только имена файлов в
# отладочных сообщениях.
# Флаги передаём КОДИРОВАННЫМИ: обычный RUSTFLAGS режется по пробелам, а путь
# проекта у нас с пробелом («Method VPN»), и половина флага улетала отдельным
# аргументом — сборка падала с «--remap-path-prefix must contain '=' between
# FROM and TO». В CARGO_ENCODED_RUSTFLAGS разделитель \x1f, пробелы внутри
# значений безопасны.
sep=$(printf '\037')
encoded="--remap-path-prefix=$HOME=~"
[ -n "${root:-}" ] && encoded="$encoded$sep--remap-path-prefix=$root=."
export CARGO_ENCODED_RUSTFLAGS="$encoded"

echo "── сборка $ARM ──"
cargo build -p method-core-ffi --release --target "$ARM"
arm_lib="$root/target/$ARM/release/$LIB"
[ -f "$arm_lib" ] || { echo "не собралось: $arm_lib" >&2; exit 1; }

intel_lib=""
if [ "$have_intel" = 1 ]; then
    echo "── сборка $INTEL ──"
    cargo build -p method-core-ffi --release --target "$INTEL"
    intel_lib="$root/target/$INTEL/release/$LIB"
    [ -f "$intel_lib" ] || { echo "не собралось: $intel_lib" >&2; exit 1; }
fi

# ── 2. Раскладка результата ──────────────────────────────────────────────────
# Старое удаляем до склейки: если вчера собиралась универсальная библиотека, а
# сегодня цель под Intel снесли, в build/ осталась бы вчерашняя двухархитектурная
# — и никто бы не заметил, что Intel-половина протухла на месяц.
mkdir -p "$out/include"
rm -f "$out/$LIB"
if [ -n "$intel_lib" ]; then
    lipo -create "$arm_lib" "$intel_lib" -output "$out/$LIB"
else
    cp "$arm_lib" "$out/$LIB"
fi
cp "$root/include/method_core.h" "$out/include/method_core.h"

# ── 3. Проверка: архитектуры ─────────────────────────────────────────────────
echo
echo "── что получилось ──"
echo "библиотека : $out/$LIB   ($(mb "$(bytes "$out/$LIB")"))"
echo "заголовок  : $out/include/method_core.h"
echo "архитектуры: $(lipo -archs "$out/$LIB" 2>/dev/null || echo '?')"
abi=$(sed -n 's/.*MC_ABI_VERSION: u32 = \([0-9]*\).*/\1/p' "$root/method-core-ffi/src/ffi.rs" | head -1)
echo "версия ABI : ${abi:-?}   (mc_abi_version() обязан вернуть это же число)"

# ── 4. Проверка: символы ─────────────────────────────────────────────────────
# `nm -gU` по самому архиву на этой сборке НЕ работает: при lto = true rustc
# оставляет в наших объектах секцию __LLVM,__bitcode, и nm от Xcode спотыкается
# о неё («Unknown attribute kind» — его LLVM старше того, которым собран Rust).
# Компоновщик эту секцию просто игнорирует, поэтому на сборку приложения это не
# влияет, но проверять символы приходится двумя другими способами — оба честнее
# чтения архива, потому что смотрят на то же, на что смотрит компоновщик.
#
# Способ первый: индекс архива (__.SYMDEF). Это ровно та таблица, по которой ld
# решает, какие члены архива подтягивать.
echo
echo "── экспортируемые символы ──"
# Имя члена-индекса у ar не одно и то же на разных системах («__.SYMDEF» или
# «__.SYMDEF SORTED»), поэтому берём первый член, а не угадываем имя: индекс в
# архиве Mach-O всегда лежит первым.
index_member=$(ar t "$arm_lib" | head -1)
symdef=$(ar p "$arm_lib" "$index_member" 2>/dev/null | strings | grep '^_mc_' | sort -u || true)
n=$(printf '%s\n' "$symdef" | grep -c '^_mc_' || true)
echo "в индексе архива: $n шт."
[ "$n" -gt 0 ] || { echo "в архиве нет ни одного символа mc_* — сборка бессмысленна" >&2; exit 1; }

# Способ второй: собрать пустую программу, заставив компоновщик втянуть КАЖДЫЙ
# экспортируемый символ (-u), и посмотреть на неё через nm. Это одновременно
# проверка компоновки (не появилось ли зависимости от фреймворка, которую в
# Xcode пришлось бы разгадывать по «undefined symbol») и честный замер того,
# сколько библиотека добавит к приложению.
if command -v cc >/dev/null 2>&1; then
    tmp=$(mktemp -d)
    trap 'rm -rf "$tmp"' EXIT
    echo 'int main(void) { return 0; }' > "$tmp/probe.c"
    forces=""
    for s in $symdef; do forces="$forces -Wl,-u,$s"; done

    cc -O2 -arch arm64 -o "$tmp/empty" "$tmp/probe.c"
    # -dead_strip: Xcode включает его в Release (DEAD_CODE_STRIPPING = YES), и
    # без него замер завышен в два с половиной раза (2,0 МБ против 818 КБ) —
    # в архиве лежит вся стандартная библиотека Rust, а до приложения доезжает
    # только затронутое.
    # shellcheck disable=SC2086
    cc -O2 -arch arm64 -Wl,-dead_strip $forces -o "$tmp/probe" "$tmp/probe.c" "$out/$LIB"

    nm_sees=$(nm -gU "$tmp/probe" 2>/dev/null | grep -c ' T _mc_' || true)
    echo "nm -gU по слинкованной программе: $nm_sees шт."
    # Расхождение означало бы, что индекс архива обещает символ, которого в коде
    # нет. Компоновка с -u такое ловит сама (ошибкой), но сказать вслух дешевле,
    # чем потом читать «undefined symbol» из Xcode.
    [ "$nm_sees" = "$n" ] || echo "!! индекс обещает $n символов, в бинаре $nm_sees" >&2
    added=$(( $(bytes "$tmp/probe") - $(bytes "$tmp/empty") ))
    echo
    echo "── размер ──"
    echo "архив на диске              : $(mb "$(bytes "$out/$LIB")")  (в приложение НЕ едет целиком)"
    echo "добавится к бинарю приложения: $(kb "$added")  (каждая экспортируемая функция втянута принудительно, -dead_strip включён)"
    echo "дополнительных фреймворков не требуется — хватает libSystem"
else
    echo "cc не найден — проверка компоновки и замер вклада пропущены"
fi

# ── 5. Предупреждение про Intel ──────────────────────────────────────────────
if [ "$have_intel" != 1 ]; then
    cat <<'WARN'


!! ВНИМАНИЕ: собрана ТОЛЬКО arm64.
   Цель x86_64-apple-darwin в системе не установлена.

   Приложение с этой библиотекой НЕ ЗАПУСТИТСЯ на Intel-маках: Rosetta
   переводит x86_64 в arm64, а не наоборот. Сборка Xcode для x86_64 упадёт
   на компоновке («building for macOS-x86_64 but attempting to link with
   file built for macOS-arm64»), поэтому у цели должно стоять ARCHS = arm64.

   Нужен Intel — поставь цель и перезапусти скрипт (сам он ничего не ставит):

       rustup target add x86_64-apple-darwin

WARN
fi

# ── 6. Что вписать в Xcode ───────────────────────────────────────────────────
# Печатаем ровно те строки, которые нужно вставить: путь до библиотеки
# набирается руками один раз, и опечатка в нём выглядит как «символы не
# найдены», а не как «путь неверный».
cat <<XCODE

── что вписать в Xcode ─────────────────────────────────────────────────────

Настройки цели приложения (Build Settings):

  LIBRARY_SEARCH_PATHS        = $out
  HEADER_SEARCH_PATHS         = $out/include
  OTHER_LDFLAGS               = -lmethod_core_ffi
  SWIFT_OBJC_BRIDGING_HEADER  = MethodVPN/MethodVPN-Bridging-Header.h
  DEAD_CODE_STRIPPING         = YES     (в Release стоит по умолчанию)

Bridging header — файл MethodVPN/MethodVPN-Bridging-Header.h из одной строки:

  #import "method_core.h"

Проект собирается XcodeGen, поэтому то же самое в project.yml (пути даны от
MacOS/, где лежит проект — тогда переезд каталога ничего не ломает):

  settings:
    LIBRARY_SEARCH_PATHS: [\$(SRCROOT)/../Core/build]
    HEADER_SEARCH_PATHS:  [\$(SRCROOT)/../Core/build/include]
    OTHER_LDFLAGS:        [-lmethod_core_ffi]
    SWIFT_OBJC_BRIDGING_HEADER: MethodVPN/MethodVPN-Bridging-Header.h

Если в цель берётся обёртка Swift (swift/MethodEngine.swift), мостовой
заголовок можно не заводить вовсе — рядом с ней лежит модуль:

  SWIFT_INCLUDE_PATHS = \$(SRCROOT)/../Core/swift/CMethodCore

и в коде \`import CMethodCore\` вместо мостового заголовка. Обёртка написана
так, что работает при обоих способах (\`#if canImport\`). Модуль ссылается на
Core/include/method_core.h напрямую, поэтому HEADER_SEARCH_PATHS в этом случае
не нужен — но LIBRARY_SEARCH_PATHS и OTHER_LDFLAGS нужны всё равно.

Замечания, каждое из которых стоит одной ошибки компоновки:

  • Библиотеку линкует ТОТ процесс, который принимает решения. Если движок
    будет жить в демоне, а не в приложении, эти же четыре строки нужны цели
    демона, а не приложения (или обеим — тогда у каждой будет свой движок и
    своё состояние, что почти наверняка не то, чего хочется).
  • Никаких фреймворков добавлять не надо: библиотека обходится libSystem.
    Если однажды понадобится Security или CoreFoundation, это сломает
    Scripts/build-macos.sh, а не сборку приложения.
  • Статика подхватывается на момент КОМПОНОВКИ. Пересобрал Rust — пересобери
    приложение; Xcode сам про изменившийся .a не узнает.
  • Заголовок в build/include — КОПИЯ. Править надо Core/include/method_core.h
    и перезапускать скрипт, иначе правка потеряется при следующей сборке.
XCODE
