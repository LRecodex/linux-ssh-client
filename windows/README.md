# LrecodexTerm (Windows)

This is the Windows version placeholder using **WPF + SSH.NET** with the same JSON session format.

Status
- UI scaffolded (sessions + files + terminal panel)
- JSON session store included
- **ConPTY embedded terminal is not implemented yet** (placeholder panel)

Next steps (Windows dev machine)
1) Open `windows/LrecodexTerm.Windows/LrecodexTerm.Windows.csproj` in Visual Studio
2) Implement ConPTY terminal host (CreatePseudoConsole + pipes)
3) Bind SFTP file list and session management to UI

Build
```
dotnet build windows/LrecodexTerm.Windows/LrecodexTerm.Windows.csproj
```

Notes
- WPF requires Windows; build/run on Windows only.
- Uses the same `sessions.json` schema as the Linux app.
