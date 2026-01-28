# LrecodexTerm (Windows)

WPF + ConPTY skeleton with the same JSON session model.

Build (Windows)
```
./build-windows.ps1
```
or
```
build-windows.bat
```

Notes
- ConPTY host implemented in `Terminal/ConPtyHost.cs`
- Current UI uses a simple textbox terminal (no ANSI rendering)
- Next steps: integrate ANSI renderer and SSH command binding
- Requires Windows OpenSSH client (`ssh`) in PATH
- Folder upload/download uses `tar` locally (available on modern Windows)
