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

Notes
- GTK# runs on X11. If you’re on Wayland, launch your session under Xorg.
- Sessions are stored at `~/.config/mintterm/sessions.json`.

Roadmap ideas
- Upload/download with progress
- Tabs for multiple sessions
- Inline file preview

License
MIT. See `LICENSE`.
