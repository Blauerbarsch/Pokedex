#!/bin/sh
# Startet DexHub im Android-Emulator, macht Bildschirmfotos und sammelt das Log (für die CI).
set -x
OUT=android-shots
mkdir -p $OUT
adb install -r DexHub-android.apk
adb logcat -c
adb shell am start -W -n de.blauerbarsch.dexhub/de.blauerbarsch.dexhub.MainActivity
sleep 25
adb exec-out screencap -p > $OUT/android-1-start.png
# Pokédex-Reiter antippen (falls die App läuft): einmal rechts wischen genügt nicht – Koordinaten grob
adb shell input tap 540 300
sleep 5
adb exec-out screencap -p > $OUT/android-2-tap.png
adb shell pidof de.blauerbarsch.dexhub > $OUT/pid.txt || echo "nicht mehr aktiv" > $OUT/pid.txt
adb logcat -d -v time > $OUT/logcat-full.txt
grep -iE "dexhub|mono|dotnet|avalonia|fatal|exception|AndroidRuntime|DOTNET" $OUT/logcat-full.txt | tail -400 > $OUT/logcat.txt
exit 0
