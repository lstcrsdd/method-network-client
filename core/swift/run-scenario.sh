#!/bin/sh
# Собрать ядро, скомпоновать с ним сценарий на Swift и запустить.
#
#   sh swift/run-scenario.sh            # release
#   sh swift/run-scenario.sh --debug    # отладочная сборка ядра
#   MC_LEAKS=1 sh swift/run-scenario.sh # прогон под leaks(1)
#
# Отдельный шаг компоновки обязателен: ядро — статическая библиотека, а
# интерпретатор Swift умеет подгружать только динамические.
set -eu

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/.." && pwd)

profile=release
cargo_flags="--release"
if [ "${1:-}" = "--debug" ]; then
    profile=debug
    cargo_flags=""
fi

cd "$root"
# shellcheck disable=SC2086
cargo build -p method-core-ffi $cargo_flags

out="$root/target/$profile/scenario"
swiftc -I "$here/CMethodCore" \
       -L "$root/target/$profile" -lmethod_core_ffi \
       "$here/MethodEngine.swift" "$here/EngineScenario.swift" \
       -o "$out"

if [ "${MC_LEAKS:-0}" = "1" ] && command -v leaks >/dev/null 2>&1; then
    MallocStackLogging=1 leaks --atExit -- "$out"
else
    "$out"
fi
