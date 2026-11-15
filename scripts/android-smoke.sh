#!/bin/sh
# Startet DexHub im Android-Emulator, macht Bildschirmfotos und sammelt das Log (für die CI).
# Wenn homebrew.gbc vorhanden ist (freies Homebrew-Spiel), wird es zusätzlich gestartet.
set -x
PKG=de.blauerbarsch.dexhub
OUT=android-shots
mkdir -p $OUT
adb install -r DexHub-android.apk
adb logcat -c

# 1) Normaler Start
adb shell am start -W -n $PKG/$PKG.MainActivity
sleep 30
adb exec-out screencap -p > $OUT/android-1-start.png

# 2) Querformat
adb shell settings put system accelerometer_rotation 0
adb shell settings put system user_rotation 1
sleep 6
adb exec-out screencap -p > $OUT/android-2-quer.png
adb shell settings put system user_rotation 0
sleep 3

# 3) Mit Spiel (Autostart über den Startparameter)
if [ -f homebrew.gbc ]; then
  adb shell am force-stop $PKG
  adb shell mkdir -p /sdcard/Android/data/$PKG/files/roms
  adb push homebrew.gbc /sdcard/Android/data/$PKG/files/roms/homebrew.gbc
  adb shell am start -W -n $PKG/$PKG.MainActivity --es autostart homebrew.gbc
  sleep 25
  adb exec-out screencap -p > $OUT/android-3-spiel.png
  adb shell input tap 300 1900
  sleep 4
  adb exec-out screencap -p > $OUT/android-4-spiel-taste.png
  adb shell settings put system user_rotation 1
  sleep 6
  adb exec-out screencap -p > $OUT/android-5-spiel-quer.png
fi

adb shell pidof $PKG > $OUT/pid.txt || echo "nicht mehr aktiv" > $OUT/pid.txt
adb logcat -d -v time > $OUT/logcat-full.txt
grep -iE "dexhub|mono|dotnet|avalonia|fatal|exception|AndroidRuntime|DOTNET|libretro" $OUT/logcat-full.txt | tail -600 > $OUT/logcat.txt
exit 0
