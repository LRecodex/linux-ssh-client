# LrecodexTerm

A lightweight GTK# SSH + SFTP client for Linux, built with .NET 8.

Features
- Embedded SSH terminal via `xterm` (XEmbed)
- SFTP file browser with path bar and refresh
- Saved sessions in JSON
- Optional per-file edit with `nano` (opens in a separate xterm)

Requirements
- .NET SDK 8.0
- GTK# 3 runtime
- `xterm`
- `ssh` client

Install deps (Ubuntu/Mint)
```
sudo apt update
sudo apt install -y gtk-sharp3 xterm openssh-client
```

Build and run
```
dotnet restore
dotnet build
dotnet run
```

AppImage build flow (one-file distribution)
1) Publish self-contained binary:
```
dotnet publish -c Release -r linux-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```

2) Package AppImage (linuxdeploy + GTK plugin required):
```
# from the project root
APPDIR="$PWD/LrecodexTerm.AppDir"
PUBLISH_DIR="$PWD/bin/Release/net8.0/linux-x64/publish"

rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/256x256/apps"
cp -a "$PUBLISH_DIR"/* "$APPDIR/usr/bin/"
mv "$APPDIR/usr/bin/SSH CLIENT" "$APPDIR/usr/bin/LrecodexTerm"
rm -f "$APPDIR/usr/bin/SSH CLIENT.pdb"

cat <<'EOF' > "$APPDIR/AppRun"
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
export PATH="$HERE/usr/bin:$PATH"
exec "$HERE/usr/bin/LrecodexTerm" "$@"
EOF
chmod +x "$APPDIR/AppRun"

cat <<'EOF' > "$APPDIR/usr/share/applications/LrecodexTerm.desktop"
[Desktop Entry]
Type=Application
Name=LrecodexTerm
Comment=SSH + SFTP client
Exec=LrecodexTerm
Icon=LrecodexTerm
Terminal=false
Categories=Network;Utility;
EOF

cat <<'EOF' > "$APPDIR/usr/share/icons/hicolor/256x256/apps/LrecodexTerm.svg"
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
EOF

if [ -x /usr/bin/xterm ]; then
  cp -a /usr/bin/xterm "$APPDIR/usr/bin/"
fi

DEPLOY_GTK_VERSION=3 /tmp/linuxdeploy-x86_64.AppImage --appdir "$APPDIR" --plugin gtk --output appimage
```

3) Share the output:
```
chmod +x LrecodexTerm-x86_64.AppImage
./LrecodexTerm-x86_64.AppImage
```

Notes
- GTK# runs on X11. If you’re on Wayland, launch your session under Xorg.
- Sessions are stored at `~/.config/mintterm/sessions.json`.

Roadmap ideas
- Upload/download with progress
- Tabs for multiple sessions
- Inline file preview

License
MIT. See `LICENSE`.
