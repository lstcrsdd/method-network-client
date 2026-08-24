#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
SDK="${ANDROID_HOME:-$HOME/Library/Android/sdk}"
JBR="/Applications/Android Studio.app/Contents/jbr/Contents/Home"
JAVA="$JBR/bin/java"
JAVAC="$JBR/bin/javac"
KEYTOOL="$JBR/bin/keytool"
export JAVA_HOME="$JBR"
export PATH="$JAVA_HOME/bin:$PATH"

PLATFORM="$SDK/platforms/android-36.1/android.jar"
BT=""
for candidate in "$SDK/build-tools/37.0.0" "$SDK/build-tools/36.1.0" "$SDK/build-tools/35.0.0"; do
  if [ -x "$candidate/aapt2" ]; then BT="$candidate"; break; fi
done
[ -n "$BT" ] || { echo "Android build-tools не найдены" >&2; exit 1; }
[ -f "$PLATFORM" ] || { echo "android.jar не найден: $PLATFORM" >&2; exit 1; }

# Версия в одном месте. Раньше она была вписана в середину вызова aapt2, и
# поднять её перед релизом означало вспомнить, где именно она зарыта.
#
# versionCode — целое, которое Android сравнивает при обновлении: меньше или
# равно установленному, и обновление не поставится. versionName видит человек.
VERSION_CODE="${METHOD_VERSION_CODE:-8}"
VERSION_NAME="${METHOD_VERSION_NAME:-0.2.4}"

SRCROOT="/tmp/method-client-android-src"
rm -rf "$SRCROOT"
mkdir -p "$SRCROOT"
cp -R "$ROOT/app/src/main/." "$SRCROOT/"

APP="$SRCROOT"
BUILD="/tmp/method-client-android-build"
OUT="$ROOT/dist"
rm -rf "$BUILD" "$OUT"
mkdir -p "$BUILD/compiled" "$BUILD/gen" "$BUILD/classes" "$BUILD/dex" "$OUT"

AAR="$ROOT/../app/libs/libbox.aar"
AAR_DIR="$BUILD/libbox-aar"
LIBBOX_JAR=""
if [ -f "$AAR" ]; then
  echo "0/6 extract libbox AAR"
  mkdir -p "$AAR_DIR"
  unzip -q "$AAR" -d "$AAR_DIR"
  LIBBOX_JAR="$AAR_DIR/classes.jar"
fi

echo "1/6 aapt2 compile"
"$BT/aapt2" compile --dir "$APP/res" -o "$BUILD/compiled/res.zip"

echo "2/6 aapt2 link"
"$BT/aapt2" link \
  -I "$PLATFORM" \
  --manifest "$APP/AndroidManifest.xml" \
  --min-sdk-version 26 \
  --target-sdk-version 36 \
  --version-code "$VERSION_CODE" \
  --version-name "$VERSION_NAME" \
  -R "$BUILD/compiled/res.zip" \
  -A "$APP/assets" \
  --auto-add-overlay \
  --java "$BUILD/gen" \
  -o "$BUILD/unsigned.apk"

echo "3/6 javac"
find "$APP/java" "$BUILD/gen" -name '*.java' > "$BUILD/sources.txt"
JAVAC_CP="$PLATFORM"
if [ -f "$LIBBOX_JAR" ]; then JAVAC_CP="$JAVAC_CP:$LIBBOX_JAR"; fi
"$JAVAC" -encoding UTF-8 -source 17 -target 17 -classpath "$JAVAC_CP" -d "$BUILD/classes" @"$BUILD/sources.txt"

echo "4/6 d8"
find "$BUILD/classes" -name '*.class' > "$BUILD/classes.txt"
"$BT/d8" --min-api 26 --output "$BUILD/dex" @"$BUILD/classes.txt"
(cd "$BUILD/dex" && zip -q -r "$BUILD/unsigned.apk" classes.dex)

if [ -f "$LIBBOX_JAR" ]; then
  echo "4.5/6 dex libbox"
  mkdir -p "$BUILD/libbox-dex"
  "$BT/d8" --min-api 26 --classpath "$PLATFORM" --output "$BUILD/libbox-dex" "$LIBBOX_JAR"
  cp "$BUILD/libbox-dex/classes.dex" "$BUILD/classes2.dex"
  (cd "$BUILD" && zip -q "$BUILD/unsigned.apk" classes2.dex)
fi

if [ -d "$AAR_DIR/jni" ]; then
  echo "4.6/6 embed libbox native libs"
  mkdir -p "$BUILD/native/lib"
  ABIS="${METHOD_ANDROID_ABIS:-arm64-v8a}"
  for abi in $ABIS; do
    if [ -f "$AAR_DIR/jni/$abi/libbox.so" ]; then
      mkdir -p "$BUILD/native/lib/$abi"
      cp "$AAR_DIR/jni/$abi/libbox.so" "$BUILD/native/lib/$abi/libbox.so"
    fi
    # Мост к общему слою на Rust: разбор ссылок и сборка конфига — одна
    # реализация на все клиенты. Собирается Core/Scripts/build-android.sh.
    case "$abi" in
      arm64-v8a)   RUST_SO="$ROOT/../../Core/target/aarch64-linux-android/release/libmethod_android.so" ;;
      armeabi-v7a) RUST_SO="$ROOT/../../Core/target/armv7-linux-androideabi/release/libmethod_android.so" ;;
      x86_64)      RUST_SO="$ROOT/../../Core/target/x86_64-linux-android/release/libmethod_android.so" ;;
      *)           RUST_SO="" ;;
    esac
    if [ -n "$RUST_SO" ] && [ -f "$RUST_SO" ]; then
      mkdir -p "$BUILD/native/lib/$abi"
      cp "$RUST_SO" "$BUILD/native/lib/$abi/libmethod_android.so"
    else
      echo "      внимание: нет моста для $abi — собери Core/Scripts/build-android.sh"
    fi
  done
  (cd "$BUILD/native" && find lib -type f -name '*.so' -print | zip -q -@ "$BUILD/unsigned.apk")
elif [ -d "$APP/lib" ]; then
  echo "4.6/6 embed native libs"
  (cd "$APP" && find lib -type f -name '*.so' -print | zip -q -@ "$BUILD/unsigned.apk")
fi

echo "5/6 zipalign"
"$BT/zipalign" -f 4 "$BUILD/unsigned.apk" "$BUILD/aligned.apk"

echo "6/6 sign"
if [ "${METHOD_RELEASE:-0}" = "1" ]; then
  # РЕЛИЗ. Подписываем ключом человека, лежащим вне репозитория.
  #
  # Пароль не берётся из переменных окружения и не передаётся аргументом:
  # аргументы видны в списке процессов любому пользователю машины, а
  # переменные наследуются дочерними процессами и попадают в дампы. apksigner
  # спрашивает пароль сам, у терминала.
  KEYSTORE="${METHOD_KEYSTORE:-$HOME/.method-keys/method-release.jks}"
  ALIAS="${METHOD_KEY_ALIAS:-method}"
  if [ ! -f "$KEYSTORE" ]; then
    echo "Релизного ключа нет: $KEYSTORE" >&2
    echo "Создайте его: ./make-release-key.sh" >&2
    exit 1
  fi

  APK="$OUT/Method-android-$VERSION_NAME.apk"
  "$BT/apksigner" sign --ks "$KEYSTORE" --ks-key-alias "$ALIAS" --out "$APK" "$BUILD/aligned.apk"
  "$BT/apksigner" verify --print-certs "$APK" | grep -iE "SHA-256 digest|verif" | sed 's/^/  /'

  echo
  echo "  Сверьте отпечаток с тем, что выдал make-release-key.sh."
  echo "  Не совпал — значит подписано ДРУГИМ ключом, и такая сборка не"
  echo "  установится поверх уже выпущенной."
else
  # ОТЛАДКА. Ключ одноразовый, генерируется на месте и в репозиторий не
  # попадает (закрыт .gitignore). Пароль общеизвестен намеренно: этим ключом
  # ничего выпускать нельзя, и притворяться, будто он что-то защищает, вредно.
  KEYSTORE="$ROOT/debug.keystore"
  if [ ! -f "$KEYSTORE" ]; then
    "$KEYTOOL" -genkeypair -v -keystore "$KEYSTORE" -storepass android -alias androiddebugkey -keypass android \
      -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=Method Debug, OU=Method, O=Method, L=Moscow, S=Moscow, C=RU" >/dev/null
  fi
  APK="$OUT/Method-android-debug.apk"
  "$BT/apksigner" sign --ks "$KEYSTORE" --ks-pass pass:android --key-pass pass:android --out "$APK" "$BUILD/aligned.apk"
  "$BT/apksigner" verify "$APK"
fi
ls -lh "$APK"
