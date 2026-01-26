using System;
using System.Collections.Generic;
using System.Diagnostics;
using Gtk;
using Renci.SshNet;

public class MainWindow : Window
{
    // Data
    private readonly List<SshSession> _sessions;
    private SshSession? _selected;
    private SftpClient? _sftp;

    // UI
    private readonly TreeView _sessionsView;
    private readonly ListStore _sessionsStore;

    private readonly TreeView _filesView;
    private readonly ListStore _filesStore;

    private readonly Entry _pathEntry;
    private readonly Button _connectBtn;
    private readonly Button _disconnectBtn;
    private readonly Button _refreshBtn;

    // Embedded terminal via XEmbed
    private Socket _termSocket;
    private Process? _termProc;
    private readonly Frame _termFrame;

    public MainWindow() : base("MintTerm (SSH + SFTP)")
    {
        SetDefaultSize(1100, 650);
        DeleteEvent += (_, args) =>
        {
            CleanupAndQuit();
            args.RetVal = true;
        };

        _sessions = SessionStore.Load();

        // Layout: Left (sessions) | Right (files + terminal)
        var root = new Paned(Orientation.Horizontal);
        Add(root);

        // ===== Left: sessions =====
        var leftBox = new Box(Orientation.Vertical, 6) { BorderWidth = 8 };
        root.Pack1(leftBox, resize: false, shrink: false);

        _sessionsStore = new ListStore(typeof(string));
        _sessionsView = new TreeView(_sessionsStore);
        _sessionsView.HeadersVisible = false;
        _sessionsView.AppendColumn("Session", new CellRendererText(), "text", 0);

        var sessionsScroll = new ScrolledWindow();
        sessionsScroll.Add(_sessionsView);
        leftBox.PackStart(new Label("Saved Sessions"), false, false, 0);
        leftBox.PackStart(sessionsScroll, true, true, 0);

        var btnRow = new Box(Orientation.Horizontal, 6);
        var addBtn = new Button("New");
        var editBtn = new Button("Edit");
        var delBtn = new Button("Delete");
        btnRow.PackStart(addBtn, true, true, 0);
        btnRow.PackStart(editBtn, true, true, 0);
        btnRow.PackStart(delBtn, true, true, 0);
        leftBox.PackStart(btnRow, false, false, 0);

        // ===== Right =====
        var right = new Box(Orientation.Vertical, 6) { BorderWidth = 8 };
        root.Pack2(right, resize: true, shrink: false);

        // Header bar (modern GTK3 layout)
        var header = new HeaderBar
        {
            Title = "MintTerm",
            Subtitle = "SSH + SFTP",
            ShowCloseButton = true
        };
        Titlebar = header;

        _connectBtn = new Button("Connect");
        _disconnectBtn = new Button("Disconnect") { Sensitive = false };
        _refreshBtn = new Button("Refresh") { Sensitive = false };
        _connectBtn.StyleContext.AddClass("suggested-action");
        _disconnectBtn.StyleContext.AddClass("destructive-action");

        header.PackStart(_connectBtn);
        header.PackStart(_disconnectBtn);
        header.PackEnd(_refreshBtn);

        // Path row
        var pathRow = new Box(Orientation.Horizontal, 6);
        _pathEntry = new Entry { Text = "/" };
        pathRow.PackStart(new Label("Remote:"), false, false, 0);
        pathRow.PackStart(_pathEntry, true, true, 0);
        right.PackStart(pathRow, false, false, 0);

        // Split: files (top) + terminal (bottom)
        var vpaned = new Paned(Orientation.Vertical);
        right.PackStart(vpaned, true, true, 0);

        // Files view
        _filesStore = new ListStore(typeof(string), typeof(string), typeof(string));
        _filesView = new TreeView(_filesStore);
        _filesView.AppendColumn("Name", new CellRendererText(), "text", 0);
        _filesView.AppendColumn("Size", new CellRendererText(), "text", 1);
        _filesView.AppendColumn("Modified", new CellRendererText(), "text", 2);

        var filesScroll = new ScrolledWindow();
        filesScroll.Add(_filesView);
        vpaned.Pack1(filesScroll, resize: true, shrink: false);

        // Terminal embed
        _termSocket = CreateTerminalSocket();
        _termFrame = new Frame("Terminal");
        _termFrame.Add(_termSocket);
        vpaned.Pack2(_termFrame, resize: false, shrink: false);

        // Events
        LoadSessionsIntoUI();

        _sessionsView.CursorChanged += (_, __) => SelectSessionFromUI();

        addBtn.Clicked += (_, __) => AddSession();
        editBtn.Clicked += (_, __) => EditSession();
        delBtn.Clicked += (_, __) => DeleteSession();

        _connectBtn.Clicked += (_, __) => Connect();
        _disconnectBtn.Clicked += (_, __) => Disconnect();
        _refreshBtn.Clicked += (_, __) => RefreshFiles();

        _filesView.RowActivated += (_, args) => OnFileActivated(args);
        _pathEntry.Activated += (_, __) => RefreshFiles();
    }

    private void LoadSessionsIntoUI()
    {
        _sessionsStore.Clear();
        foreach (var s in _sessions)
        {
            _sessionsStore.AppendValues(s.Name);
        }
    }

    private void SelectSessionFromUI()
    {
        if (!_sessionsView.Selection.GetSelected(out TreeIter iter)) return;
        var name = (string)_sessionsStore.GetValue(iter, 0);

        _selected = _sessions.Find(s => s.Name == name);
        if (_selected != null)
        {
            _pathEntry.Text = _selected.RemotePath;
        }
    }

    private void AddSession()
    {
        var s = new SshSession { Name = "New Session", Host = "server.com", Username = "user", RemotePath = "/home/user" };
        if (EditDialog(s, isNew: true))
        {
            _sessions.Add(s);
            SessionStore.Save(_sessions);
            LoadSessionsIntoUI();
        }
    }

    private void EditSession()
    {
        if (_selected == null) return;
        var copy = Clone(_selected);
        if (EditDialog(copy, isNew: false))
        {
            // overwrite selected
            _selected.Name = copy.Name;
            _selected.Host = copy.Host;
            _selected.Port = copy.Port;
            _selected.Username = copy.Username;
            _selected.Password = copy.Password;
            _selected.PrivateKeyPath = copy.PrivateKeyPath;
            _selected.PrivateKeyPassphrase = copy.PrivateKeyPassphrase;
            _selected.RemotePath = copy.RemotePath;

            SessionStore.Save(_sessions);
            LoadSessionsIntoUI();
        }
    }

    private void DeleteSession()
    {
        if (_selected == null) return;
        using var md = new MessageDialog(this, DialogFlags.Modal, MessageType.Question, ButtonsType.YesNo,
            $"Delete session '{_selected.Name}'?");
        var resp = (ResponseType)md.Run();

        if (resp == ResponseType.Yes)
        {
            _sessions.Remove(_selected);
            _selected = null;
            SessionStore.Save(_sessions);
            LoadSessionsIntoUI();
        }
    }

    // ===== Connect/Disconnect =====

    private void Connect()
    {
        if (_selected == null)
        {
            Toast("Select a session first.");
            return;
        }

        try
        {
            Disconnect();

            // 1) Terminal: embed xterm and run ssh (defer until socket is realized)
            ScheduleEmbeddedSshTerminal(_selected, attempt: 0);

            // 2) SFTP: connect using SSH.NET
            _sftp = CreateSftpClient(_selected);
            _sftp.Connect();

            _disconnectBtn.Sensitive = true;
            _refreshBtn.Sensitive = true;
            _connectBtn.Sensitive = false;

            // show files immediately
            RefreshFiles();
        }
        catch (Exception ex)
        {
            Toast("Connect failed: " + ex.Message);
            Disconnect();
        }
    }

    private void Disconnect()
    {
        try
        {
            if (_sftp != null)
            {
                if (_sftp.IsConnected) _sftp.Disconnect();
                _sftp.Dispose();
                _sftp = null;
            }
        }
        catch
        {
            // ignore
        }

        StopEmbeddedTerminal();

        _disconnectBtn.Sensitive = false;
        _refreshBtn.Sensitive = false;
        _connectBtn.Sensitive = true;
        _filesStore.Clear();
    }

    private void RefreshFiles()
    {
        if (_sftp == null || !_sftp.IsConnected) return;

        try
        {
            var path = _pathEntry.Text.Trim();
            if (string.IsNullOrWhiteSpace(path)) path = "/";

            var list = _sftp.ListDirectory(path);

            _filesStore.Clear();
            if (path != "/")
            {
                _filesStore.AppendValues("..", "-", "-");
            }

            foreach (var f in list)
            {
                if (f.Name == ".") continue;
                if (f.Name == "..") continue;

                var size = f.IsDirectory ? "-" : f.Length.ToString();
                var mod = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                _filesStore.AppendValues(f.Name, size, mod);
            }
        }
        catch (Exception ex)
        {
            Toast("Refresh failed: " + ex.Message);
        }
    }

    private void OnFileActivated(RowActivatedArgs args)
    {
        if (_sftp == null || !_sftp.IsConnected) return;
        if (!_filesView.Selection.GetSelected(out TreeIter iter)) return;

        var name = (string)_filesStore.GetValue(iter, 0);
        if (name == "..")
        {
            _pathEntry.Text = ParentPath(_pathEntry.Text);
            RefreshFiles();
            return;
        }

        var newPath = CombineRemote(_pathEntry.Text, name);

        try
        {
            var attrs = _sftp.GetAttributes(newPath);
            if (attrs.IsDirectory)
            {
                _pathEntry.Text = newPath;
                RefreshFiles();
            }
            else
            {
                OpenFileInNano(newPath);
            }
        }
        catch
        {
            // ignore (file or no perm)
        }
    }

    // ===== Embedded terminal (xterm -into <XID>) =====
    private void ScheduleEmbeddedSshTerminal(SshSession s, int attempt)
    {
        GLib.Idle.Add(() =>
        {
            try
            {
                StartEmbeddedSshTerminal(s);
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Terminal socket not ready", StringComparison.OrdinalIgnoreCase) && attempt < 5)
                {
                    ScheduleEmbeddedSshTerminal(s, attempt + 1);
                    return false;
                }
                Toast("Terminal start failed: " + ex.Message);
            }
            return false;
        });
    }

    private void StartEmbeddedSshTerminal(SshSession s)
    {
        RecreateTerminalSocket();

        if (!IsRealized)
        {
            ShowAll();
        }

        if (!_termSocket.Visible)
        {
            _termSocket.Show();
        }

        // Let GTK finish realize/map before grabbing the XID
        for (var i = 0; i < 20 && !_termSocket.IsRealized; i++)
        {
            while (Application.EventsPending())
            {
                Application.RunIteration();
            }
        }

        var xid = _termSocket.Id;
        if (xid == 0) throw new Exception("Terminal socket not ready.");

        var sshTarget = $"{s.Username}@{s.Host}";
        var portPart = s.Port != 22 ? $"-p {s.Port}" : "";

        var cmd = $"xterm -into {xid} -fa 'Monospace' -fs 11 -e ssh {portPart} {sshTarget}";
        _termProc = Process.Start(new ProcessStartInfo("/bin/bash", $"-lc \"{cmd}\"")
        {
            UseShellExecute = false
        });

        if (_termProc != null)
        {
            _termProc.EnableRaisingEvents = true;
            _termProc.Exited += (_, __) =>
            {
                Application.Invoke((_, __) =>
                {
                    StopEmbeddedTerminal(recreateSocket: true);
                    _disconnectBtn.Sensitive = false;
                    _refreshBtn.Sensitive = false;
                    _connectBtn.Sensitive = true;
                });
            };
        }
    }

    private void StopEmbeddedTerminal(bool recreateSocket = false)
    {
        try
        {
            if (_termProc != null && !_termProc.HasExited)
            {
                _termProc.Kill();
            }
        }
        catch
        {
            // ignore
        }
        _termProc = null;

        if (recreateSocket)
        {
            RecreateTerminalSocket();
        }
    }

    private void OpenFileInNano(string remotePath)
    {
        if (_selected == null) return;

        var sshTarget = $"{_selected.Username}@{_selected.Host}";
        var portPart = _selected.Port != 22 ? $"-p {_selected.Port}" : "";
        var pathArg = QuoteForShell(remotePath);

        // Launch a separate xterm running nano for the selected file
        var cmd = $"xterm -fa 'Monospace' -fs 11 -e ssh -t {portPart} {sshTarget} nano {pathArg}";
        Process.Start(new ProcessStartInfo("/bin/bash", $"-lc \"{cmd}\"")
        {
            UseShellExecute = false
        });
    }

    private Socket CreateTerminalSocket()
    {
        var socket = new Socket();
        socket.SetSizeRequest(-1, 260);
        return socket;
    }

    private void RecreateTerminalSocket()
    {
        if (_termSocket != null)
        {
            _termFrame.Remove(_termSocket);
            _termSocket.Destroy();
        }

        _termSocket = CreateTerminalSocket();
        _termFrame.Add(_termSocket);
        _termFrame.ShowAll();
    }

    // ===== SFTP Client =====
    private SftpClient CreateSftpClient(SshSession s)
    {
        if (!string.IsNullOrWhiteSpace(s.PrivateKeyPath))
        {
            var keyFile = string.IsNullOrWhiteSpace(s.PrivateKeyPassphrase)
                ? new PrivateKeyFile(s.PrivateKeyPath)
                : new PrivateKeyFile(s.PrivateKeyPath, s.PrivateKeyPassphrase);

            var auth = new PrivateKeyAuthenticationMethod(s.Username, keyFile);
            var conn = new ConnectionInfo(s.Host, s.Port, s.Username, auth);
            return new SftpClient(conn);
        }

        if (string.IsNullOrWhiteSpace(s.Password))
        {
            throw new Exception("No password and no private key set for SFTP.");
        }

        return new SftpClient(s.Host, s.Port, s.Username, s.Password);
    }

    // ===== Session edit dialog =====
    private bool EditDialog(SshSession s, bool isNew)
    {
        using var d = new Dialog(isNew ? "New Session" : "Edit Session", this, DialogFlags.Modal);
        d.AddButton("Cancel", ResponseType.Cancel);
        d.AddButton("Save", ResponseType.Ok);

        var grid = new Grid { BorderWidth = 10, RowSpacing = 6, ColumnSpacing = 8 };

        var name = new Entry(s.Name);
        var host = new Entry(s.Host);
        var port = new SpinButton(1, 65535, 1) { Value = s.Port };
        var user = new Entry(s.Username);
        var pass = new Entry(s.Password ?? string.Empty) { Visibility = false };
        var key = new Entry(s.PrivateKeyPath ?? string.Empty);
        var remote = new Entry(s.RemotePath);

        AttachRow(grid, 0, "Name", name);
        AttachRow(grid, 1, "Host", host);
        AttachRow(grid, 2, "Port", port);
        AttachRow(grid, 3, "Username", user);
        AttachRow(grid, 4, "Password (optional)", pass);
        AttachRow(grid, 5, "Private key path (optional)", key);
        AttachRow(grid, 6, "Remote start path", remote);

        d.ContentArea.PackStart(grid, true, true, 0);
        d.ShowAll();

        var resp = (ResponseType)d.Run();
        if (resp == ResponseType.Ok)
        {
            s.Name = name.Text.Trim();
            s.Host = host.Text.Trim();
            s.Port = (int)port.Value;
            s.Username = user.Text.Trim();

            s.Password = string.IsNullOrWhiteSpace(pass.Text) ? null : pass.Text;
            s.PrivateKeyPath = string.IsNullOrWhiteSpace(key.Text) ? null : key.Text.Trim();

            s.RemotePath = string.IsNullOrWhiteSpace(remote.Text) ? "/" : remote.Text.Trim();

            return true;
        }

        return false;
    }

    private static void AttachRow(Grid t, int row, string label, Widget w)
    {
        t.Attach(new Label(label) { Xalign = 0 }, 0, row, 1, 1);
        t.Attach(w, 1, row, 1, 1);
    }

    private static SshSession Clone(SshSession s) => new()
    {
        Name = s.Name,
        Host = s.Host,
        Port = s.Port,
        Username = s.Username,
        Password = s.Password,
        PrivateKeyPath = s.PrivateKeyPath,
        PrivateKeyPassphrase = s.PrivateKeyPassphrase,
        RemotePath = s.RemotePath
    };

    private static string CombineRemote(string basePath, string name)
    {
        basePath = (basePath ?? "/").Trim();
        if (!basePath.StartsWith("/")) basePath = "/" + basePath;
        if (basePath.EndsWith("/")) return basePath + name;
        return basePath + "/" + name;
    }

    private static string ParentPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/") return "/";
        path = path.TrimEnd('/');
        var idx = path.LastIndexOf('/');
        if (idx <= 0) return "/";
        return path.Substring(0, idx);
    }

    private static string QuoteForShell(string s)
    {
        return "'" + s.Replace("'", "'\"'\"'") + "'";
    }

    private void Toast(string msg)
    {
        using var md = new MessageDialog(this, DialogFlags.Modal, MessageType.Info, ButtonsType.Ok, msg);
        md.Run();
    }

    private void CleanupAndQuit()
    {
        Disconnect();
        Application.Quit();
    }
}
