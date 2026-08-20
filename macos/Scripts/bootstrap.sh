#!/usr/bin/env bash
# Bootstrap окружения для сборки Method VPN (macOS).
# Идемпотентно: можно запускать повторно.
set -euo pipefail

SB_VERSION="1.13.13"
XCODEGEN_VERSION="2.45.4"
ARCH="arm64"   # Apple Silicon

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RES="$ROOT/Resources"
TOOLS="$ROOT/.tools"
mkdir -p "$RES" "$TOOLS"

say() { printf "\033[1;36m==>\033[0m %s\n" "$*"; }
warn() { printf "\033[1;33m!!\033[0m %s\n" "$*"; }

# 1) Лицензия Xcode -----------------------------------------------------------
if ! /usr/bin/xcrun clang --version >/dev/null 2>&1; then
  warn "Лицензия Xcode не принята. Выполни один раз:"
  warn "    sudo xcodebuild -license accept"
  exit 1
fi
say "Xcode toolchain OK ($(xcodebuild -version | head -1))"

# 2) sing-box (ядро, darwin-${ARCH}) -----------------------------------------
if [ -x "$RES/sing-box" ] && "$RES/sing-box" version 2>/dev/null | grep -q "$SB_VERSION"; then
  say "sing-box $SB_VERSION уже на месте"
else
  say "Скачиваю sing-box $SB_VERSION ($ARCH)…"
  url="https://github.com/SagerNet/sing-box/releases/download/v${SB_VERSION}/sing-box-${SB_VERSION}-darwin-${ARCH}.tar.gz"
  tmp="$(mktemp -d)"
  curl -sL --fail -o "$tmp/sb.tgz" "$url"
  tar -xzf "$tmp/sb.tgz" -C "$tmp"
  cp "$(find "$tmp" -name sing-box -type f | head -1)" "$RES/sing-box"
  chmod +x "$RES/sing-box"
  rm -rf "$tmp"
  say "sing-box: $("$RES/sing-box" version | head -1)"
fi

# 3) XcodeGen -----------------------------------------------------------------
if [ -x "$TOOLS/xcodegen/bin/xcodegen" ]; then
  say "XcodeGen уже на месте"
else
  say "Скачиваю XcodeGen ${XCODEGEN_VERSION}…"
  url="https://github.com/yonaskolb/XcodeGen/releases/download/${XCODEGEN_VERSION}/xcodegen.zip"
  tmp="$(mktemp -d)"
  curl -sL --fail -o "$tmp/xg.zip" "$url"
  unzip -q "$tmp/xg.zip" -d "$tmp"
  rm -rf "$TOOLS/xcodegen"
  mv "$tmp/xcodegen" "$TOOLS/xcodegen"
  chmod +x "$TOOLS/xcodegen/bin/xcodegen"
  rm -rf "$tmp"
  say "XcodeGen: $("$TOOLS/xcodegen/bin/xcodegen" --version)"
fi

say "Готово. Дальше:"
echo "    $TOOLS/xcodegen/bin/xcodegen generate    # сгенерировать .xcodeproj"
echo "    open MethodVPN.xcodeproj"
