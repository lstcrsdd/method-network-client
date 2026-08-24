#!/usr/bin/env bash
# Сборка общего слоя под Android.
#
# Отдельно от build-macos.sh намеренно: там своя логика толстых библиотек и
# заголовка для Swift, здесь — свой набор архитектур и линковка через NDK.
# Общее у них только имя крейта.
set -eu

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/.." && pwd)"
out="$root/build/android"

SDK="${ANDROID_HOME:-$HOME/Library/Android/sdk}"
NDK="${ANDROID_NDK_HOME:-$(ls -d "$SDK"/ndk/* 2>/dev/null | sort -V | tail -1)}"
[ -d "$NDK" ] || { echo "NDK не найден: поставь через sdkmanager 'ndk;29.0.14206865'" >&2; exit 1; }

host="darwin-x86_64"
[ "$(uname -s)" = "Linux" ] && host="linux-x86_64"
bin="$NDK/toolchains/llvm/prebuilt/$host/bin"

# Минимальный уровень API — 26, как у самого приложения (minSdkVersion 26).
# Ставить выше нельзя: библиотека не запустится там, где запускается APK.
API=26

# Архитектуры. arm64 обязателен, armv7 — для старых телефонов, они ещё живы.
# x86_64 нужен эмулятору: без него разработка вслепую.
targets="aarch64-linux-android:aarch64-linux-android$API-clang:arm64-v8a
armv7-linux-androideabi:armv7a-linux-androideabi$API-clang:armeabi-v7a
x86_64-linux-android:x86_64-linux-android$API-clang:x86_64"

mkdir -p "$out"
for row in $targets; do
    triple="${row%%:*}"; rest="${row#*:}"
    compiler="${rest%%:*}"; abi="${rest##*:}"
    cc="$bin/$compiler"
    if [ ! -x "$cc" ]; then
        echo "── пропуск $abi: нет $compiler"
        continue
    fi
    rustup target list --installed | grep -q "^$triple$" || {
        echo "── пропуск $abi: нет цели Rust ($triple). rustup target add $triple"
        continue
    }
    echo "── сборка $abi"
    env \
        "CARGO_TARGET_$(echo "$triple" | tr 'a-z-' 'A-Z_')_LINKER=$cc" \
        "CC_$(echo "$triple" | tr '-' '_')=$cc" \
        "AR_$(echo "$triple" | tr '-' '_')=$bin/llvm-ar" \
        cargo build -p method-core-ffi -p method-android --release --target "$triple" --manifest-path "$root/Cargo.toml"
    mkdir -p "$out/$abi"
    cp "$root/target/$triple/release/libmethod_core_ffi.a" "$out/$abi/"
    # Мост в Java собирается ЗДЕСЬ ЖЕ. Раньше собиралась только статическая
    # библиотека для C, а .so с мостом оставалась от прошлого раза — сборка APK
    # молча брала её из target и клала в пакет. Проверено 2026-08-24: правка
    # общего слоя не доезжала до телефона, при этом всё собиралось «успешно»,
    # и час ушёл на поиск ошибки, которой в исходниках уже не было.
    cp "$root/target/$triple/release/libmethod_android.so" "$out/$abi/"
done

cp "$root/include/method_core.h" "$out/"
echo
echo "готово: $out"
echo "Линковать с -lm: движок зовёт exp2f, а математика на Android отдельной"
echo "библиотекой — без неё компоновка падает на undefined symbol."
