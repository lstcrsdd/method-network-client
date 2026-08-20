#!/bin/sh
set -eu

ACTION="${1:-install}"
LABEL="network.method.helper"
APP="/Applications/MethodVPN.app"
HELPER="${APP}/Contents/MacOS/network.method.helper"
ENGINE="${APP}/Contents/Resources/sing-box"
PLIST="/Library/LaunchDaemons/${LABEL}.plist"
STATE_DIR="/Library/Application Support/MethodVPN"
REQUIREMENT="${STATE_DIR}/client.requirement"

if [ "${ACTION}" = "uninstall" ]; then
    /bin/launchctl bootout "system/${LABEL}" 2>/dev/null || true
    /bin/launchctl bootout system "${PLIST}" 2>/dev/null || true
    /bin/rm -f "${PLIST}"
    /bin/rm -f "${REQUIREMENT}"
    exit 0
fi

if [ "${ACTION}" != "install" ]; then
    echo "Неизвестное действие: ${ACTION}" >&2
    exit 64
fi

if [ ! -x "${HELPER}" ] || [ ! -x "${ENGINE}" ]; then
    echo "В MethodVPN.app отсутствует helper или sing-box" >&2
    exit 1
fi

REQ_TMP="$(/usr/bin/mktemp /tmp/network.method.requirement.XXXXXX)"
PLIST_TMP="$(/usr/bin/mktemp /tmp/network.method.helper.XXXXXX)"
trap '/bin/rm -f "${PLIST_TMP:-}" "${REQ_TMP:-}"' EXIT
/usr/bin/codesign -dr - "${APP}" 2>&1 \
    | /usr/bin/sed -n 's/^# designated => //p' > "${REQ_TMP}"
if [ ! -s "${REQ_TMP}" ]; then
    echo "Не удалось получить designated requirement приложения" >&2
    exit 1
fi

/bin/cat > "${PLIST_TMP}" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>${LABEL}</string>
    <key>ProgramArguments</key>
    <array>
        <string>${HELPER}</string>
    </array>
    <key>MachServices</key>
    <dict>
        <key>${LABEL}</key>
        <true/>
    </dict>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>ProcessType</key>
    <string>Interactive</string>
</dict>
</plist>
PLIST

/usr/bin/plutil -lint "${PLIST_TMP}" >/dev/null

# Меняем рабочую службу только после полной проверки нового комплекта.
/bin/launchctl bootout "system/${LABEL}" 2>/dev/null || true
/bin/launchctl bootout system "${PLIST}" 2>/dev/null || true

/bin/mkdir -p "${STATE_DIR}"
/usr/sbin/chown root:wheel "${STATE_DIR}"
/bin/chmod 700 "${STATE_DIR}"
/usr/bin/install -o root -g wheel -m 600 "${REQ_TMP}" "${REQUIREMENT}"
/usr/sbin/chown root:wheel "${HELPER}" "${ENGINE}"
/bin/chmod +x "${HELPER}" "${ENGINE}"
/usr/bin/install -o root -g wheel -m 644 "${PLIST_TMP}" "${PLIST}"
/bin/launchctl bootstrap system "${PLIST}"
/bin/launchctl kickstart -k "system/${LABEL}"

exit 0
