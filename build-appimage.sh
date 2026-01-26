#!/usr/bin/env bash
set -euo pipefail

APPDIR="${PWD}/LrecodexTerm.AppDir"
PUBLISH_DIR="${PWD}/bin/Release/net8.0/linux-x64/publish"
LINUXDEPLOY_BIN="/tmp/linuxdeploy-x86_64.AppImage"
GTK_PLUGIN="/tmp/linuxdeploy-plugin-gtk"

# Publish self-contained build

dotnet publish -c Release -r linux-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true

# Prepare AppDir
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/256x256/apps"
cp -a "$PUBLISH_DIR"/* "$APPDIR/usr/bin/"

# Rename binary (AppImage name & Exec)
if [ -f "$APPDIR/usr/bin/SSH CLIENT" ]; then
  mv "$APPDIR/usr/bin/SSH CLIENT" "$APPDIR/usr/bin/LrecodexTerm"
fi
rm -f "$APPDIR/usr/bin/SSH CLIENT.pdb"

# AppRun
cat <<'EORUN' > "$APPDIR/AppRun"
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
export PATH="$HERE/usr/bin:$PATH"
exec "$HERE/usr/bin/LrecodexTerm" "$@"
EORUN
chmod +x "$APPDIR/AppRun"

# Desktop file
cat <<'EODESK' > "$APPDIR/usr/share/applications/LrecodexTerm.desktop"
[Desktop Entry]
Type=Application
Name=LrecodexTerm
Comment=SSH + SFTP client
Exec=LrecodexTerm
Icon=LrecodexTerm
Terminal=false
Categories=Network;Utility;
EODESK

# Icon
cat <<'EOSVG' > "$APPDIR/usr/share/icons/hicolor/256x256/apps/LrecodexTerm.svg"
<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <defs>
    <linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#3b82f6"/>
      <stop offset="1" stop-color="#1e293b"/>
    </linearGradient>
  </defs>
  <rect x="16" y="16" width="224" height="224" rx="36" fill="url(#g)"/>
  <rect x="48" y="64" width="160" height="128" rx="12" fill="#0b1020"/>
  <path d="M72 104 L104 128 L72 152" fill="none" stroke="#60a5fa" stroke-width="16" stroke-linecap="round" stroke-linejoin="round"/>
  <rect x="120" y="144" width="64" height="12" rx="6" fill="#93c5fd"/>
</svg>
EOSVG

# Bundle xterm if present
if [ -x /usr/bin/xterm ]; then
  cp -a /usr/bin/xterm "$APPDIR/usr/bin/"
else
  echo "Warning: /usr/bin/xterm not found; embedded terminal will fail." >&2
fi

# Auto-download linuxdeploy and GTK plugin if missing
if [ ! -x "$LINUXDEPLOY_BIN" ]; then
  echo "Downloading linuxdeploy..."
  curl -L -o "$LINUXDEPLOY_BIN" https://github.com/linuxdeploy/linuxdeploy/releases/download/continuous/linuxdeploy-x86_64.AppImage
  chmod +x "$LINUXDEPLOY_BIN"
fi
if [ ! -x "$GTK_PLUGIN" ]; then
  echo "Downloading linuxdeploy GTK plugin..."
  curl -L -o "$GTK_PLUGIN" https://raw.githubusercontent.com/linuxdeploy/linuxdeploy-plugin-gtk/master/linuxdeploy-plugin-gtk.sh
  chmod +x "$GTK_PLUGIN"
fi

# Build AppImage
DEPLOY_GTK_VERSION=3 "$LINUXDEPLOY_BIN" --appdir "$APPDIR" --plugin gtk --output appimage

echo "AppImage created: ${PWD}/LrecodexTerm-x86_64.AppImage"
