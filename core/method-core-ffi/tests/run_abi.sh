#!/bin/sh
# Собрать статическую библиотеку, скомпоновать с ней программу на C и
# запустить её. Cargo сам такого не делает: `cargo test` знает только про
# Rust, а проверять надо именно то, что видит компилятор C.
#
#   sh method-core-ffi/tests/run_abi.sh            # отладочная сборка
#   sh method-core-ffi/tests/run_abi.sh --release  # с оптимизацией
#
# Если в системе есть leaks(1) (macOS), прогон повторяется под ним:
#   MC_LEAKS=1 sh method-core-ffi/tests/run_abi.sh
set -eu

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)

profile=debug
cargo_flags=""
if [ "${1:-}" = "--release" ]; then
    profile=release
    cargo_flags="--release"
fi

cd "$root"
# shellcheck disable=SC2086
cargo build -p method-core-ffi --features test-panic $cargo_flags

lib="$root/target/$profile/libmethod_core_ffi.a"
[ -f "$lib" ] || { echo "нет библиотеки: $lib" >&2; exit 1; }

out="$root/target/$profile/abi_test"
# Никаких дополнительных фреймворков: библиотека обходится libSystem. Если
# когда-нибудь появится зависимость от CoreFoundation или Security, компоновка
# сломается здесь, а не в Xcode у того, кто будет интегрировать.
cc -std=c11 -Wall -Wextra -Werror -g -DMC_HAVE_TEST_PANIC \
   -I "$root/include" \
   -o "$out" "$here/abi.c" "$lib"

echo "── прогон $out ──"
if [ "${MC_LEAKS:-0}" = "1" ] && command -v leaks >/dev/null 2>&1; then
    MallocStackLogging=1 leaks --atExit -- "$out"
else
    "$out"
fi
