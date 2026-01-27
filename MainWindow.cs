using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    private readonly TreeModelFilter _sessionsFilter;
    private readonly Entry _sessionSearch;

    private readonly TreeView _filesView;
    private readonly ListStore _filesStore;
    private readonly TreeModelSort _filesSort;

    private readonly Entry _pathEntry;
    private readonly Button _connectBtn;
    private readonly Button _disconnectBtn;
    private readonly Button _refreshBtn;
    private readonly Button _syncBtn;
    private readonly Label _statusLabel;
    private readonly Label _filesCountLabel;

    // Embedded terminal via XEmbed
    private Socket _termSocket;
    private Process? _termProc;
    private readonly Frame _termFrame;

    public MainWindow() : base("LrecodexTerm (SSH + SFTP)")
    {
        SetDefaultSize(1120, 680);
        DeleteEvent += (_, args) =>
        {
            CleanupAndQuit();
            args.RetVal = true;
        };

        SetWindowIcon();
        ApplyCss();

        _sessions = SessionStore.Load();

        // Layout: Left (sessions) | Right (files + terminal)
        var root = new Paned(Orientation.Horizontal);
        Add(root);
        root.Position = 280;

        // ===== Left: sessions =====
        var leftBox = new Box(Orientation.Vertical, 8) { BorderWidth = 12 };
        leftBox.StyleContext.AddClass("sidebar");
        root.Pack1(leftBox, resize: false, shrink: false);

        _sessionsStore = new ListStore(typeof(string));
        _sessionsFilter = new TreeModelFilter(_sessionsStore, null);
        _sessionsView = new TreeView(_sessionsFilter);
        _sessionsView.HeadersVisible = false;
        _sessionsView.AppendColumn("Session", new CellRendererText(), "text", 0);

        var sessionsHeader = new Box(Orientation.Horizontal, 6);
        sessionsHeader.PackStart(new Label("<b>Sessions</b>") { UseMarkup = true, Xalign = 0 }, true, true, 0);
        var addHeaderBtn = MakeIconButton("list-add-symbolic", "New");
        sessionsHeader.PackEnd(addHeaderBtn, false, false, 0);

        _sessionSearch = new Entry { PlaceholderText = "Filter sessions..." };
        _sessionSearch.StyleContext.AddClass("search-entry");
        _sessionSearch.Changed += (_, __) => _sessionsFilter.Refilter();
        _sessionsFilter.VisibleFunc = (model, iter) =>
        {
            var name = (string)model.GetValue(iter, 0);
            var q = _sessionSearch.Text?.Trim();
            if (string.IsNullOrEmpty(q)) return true;
            return name.Contains(q, StringComparison.OrdinalIgnoreCase);
        };

        var sessionsScroll = new ScrolledWindow();
        sessionsScroll.Add(_sessionsView);
        leftBox.PackStart(sessionsHeader, false, false, 0);
        leftBox.PackStart(_sessionSearch, false, false, 0);
        leftBox.PackStart(sessionsScroll, true, true, 0);

        var btnRow = new Box(Orientation.Horizontal, 6);
        var addBtn = MakeIconButton("list-add-symbolic", "New");
        var editBtn = MakeIconButton("document-edit-symbolic", "Edit");
        var delBtn = MakeIconButton("user-trash-symbolic", "Delete");
        btnRow.PackStart(addBtn, true, true, 0);
        btnRow.PackStart(editBtn, true, true, 0);
        btnRow.PackStart(delBtn, true, true, 0);
        leftBox.PackStart(btnRow, false, false, 0);

        // ===== Right =====
        var right = new Box(Orientation.Vertical, 8) { BorderWidth = 12 };
        right.StyleContext.AddClass("content");
        root.Pack2(right, resize: true, shrink: false);

        // Header bar (modern GTK3 layout)
        var header = new HeaderBar
        {
            Title = "LrecodexTerm",
            Subtitle = "SSH + SFTP",
            ShowCloseButton = true
        };
        Titlebar = header;

        var headerIcon = CreateHeaderIcon();
        if (headerIcon != null)
        {
            var headerLeftBox = new Box(Orientation.Horizontal, 6);
            headerLeftBox.PackStart(headerIcon, false, false, 0);
            header.PackStart(headerLeftBox);
            headerIcon.Show();
            headerLeftBox.ShowAll();
        }

        _connectBtn = MakeIconButton("network-connect-symbolic", "Connect");
        _disconnectBtn = MakeIconButton("network-disconnect-symbolic", "Disconnect");
        _refreshBtn = MakeIconButton("view-refresh-symbolic", "Refresh");
        _syncBtn = MakeIconButton("view-refresh-symbolic", "Sync");
        _disconnectBtn.Sensitive = false;
        _refreshBtn.Sensitive = false;
        _syncBtn.Sensitive = false;
        _connectBtn.StyleContext.AddClass("suggested-action");
        _disconnectBtn.StyleContext.AddClass("destructive-action");

        header.PackStart(_connectBtn);
        header.PackStart(_disconnectBtn);
        header.PackEnd(_refreshBtn);
        header.PackEnd(_syncBtn);

        // Path row
        var pathRow = new Box(Orientation.Horizontal, 6);
        _pathEntry = new Entry { Text = "/" };
        _pathEntry.StyleContext.AddClass("path-entry");
        pathRow.PackStart(new Label("Remote:"), false, false, 0);
        pathRow.PackStart(_pathEntry, true, true, 0);
        right.PackStart(pathRow, false, false, 0);

        // Split: files (top) + terminal (bottom)
        var vpaned = new Paned(Orientation.Vertical);
        right.PackStart(vpaned, true, true, 0);

        // Files view
        _filesStore = new ListStore(typeof(string), typeof(string), typeof(string), typeof(string));
        _filesSort = new TreeModelSort(_filesStore);
        _filesView = new TreeView(_filesSort);
        _filesView.AddEvents((int)Gdk.EventMask.ButtonPressMask);
        var iconRenderer = new CellRendererPixbuf();
        var nameRenderer = new CellRendererText();
        var nameCol = new TreeViewColumn { Title = "Name" };
        nameCol.PackStart(iconRenderer, false);
        nameCol.PackStart(nameRenderer, true);
        nameCol.AddAttribute(iconRenderer, "icon-name", 0);
        nameCol.AddAttribute(nameRenderer, "text", 1);
        nameCol.SortColumnId = 1;
        nameCol.SortIndicator = true;
        _filesView.AppendColumn(nameCol);
        var sizeCol = new TreeViewColumn("Size", new CellRendererText(), "text", 2) { SortColumnId = 2, SortIndicator = true };
        var modCol = new TreeViewColumn("Modified", new CellRendererText(), "text", 3) { SortColumnId = 3, SortIndicator = true };
        _filesView.AppendColumn(sizeCol);
        _filesView.AppendColumn(modCol);

        var filesScroll = new ScrolledWindow();
        filesScroll.Add(_filesView);
        vpaned.Pack1(filesScroll, resize: true, shrink: false);

        // Terminal embed
        _termSocket = CreateTerminalSocket();
        _termFrame = new Frame("Terminal");
        _termFrame.Add(_termSocket);
        vpaned.Pack2(_termFrame, resize: false, shrink: false);

        // Status bar
        var statusRow = new Box(Orientation.Horizontal, 6);
        statusRow.StyleContext.AddClass("statusbar");
        _statusLabel = new Label("Disconnected") { Xalign = 0 };
        _filesCountLabel = new Label("0 items") { Xalign = 1 };
        statusRow.PackStart(_statusLabel, true, true, 0);
        statusRow.PackEnd(_filesCountLabel, false, false, 0);
        right.PackStart(statusRow, false, false, 0);

        // Events
        LoadSessionsIntoUI();

        _sessionsView.CursorChanged += (_, __) => SelectSessionFromUI();

        addHeaderBtn.Clicked += (_, __) => AddSession();
        addBtn.Clicked += (_, __) => AddSession();
        editBtn.Clicked += (_, __) => EditSession();
        delBtn.Clicked += (_, __) => DeleteSession();

        _connectBtn.Clicked += (_, __) => Connect();
        _disconnectBtn.Clicked += (_, __) => Disconnect();
        _refreshBtn.Clicked += (_, __) => RefreshFiles();
        _syncBtn.Clicked += (_, __) => SyncPathFromTerminal();

        _filesView.RowActivated += (_, args) => OnFileActivated(args);
        _filesView.ButtonPressEvent += OnFilesButtonPress;
        _pathEntry.Activated += (_, __) => RefreshFiles();
    }

    private void LoadSessionsIntoUI()
    {
        _sessionsStore.Clear();
        foreach (var s in _sessions)
        {
            _sessionsStore.AppendValues(s.Name);
        }
        _sessionsFilter.Refilter();
    }

    private void SelectSessionFromUI()
    {
        if (!_sessionsView.Selection.GetSelected(out TreeIter iter)) return;
        var childIter = _sessionsFilter.ConvertIterToChildIter(iter);
        var name = (string)_sessionsStore.GetValue(childIter, 0);

        _selected = _sessions.Find(s => s.Name == name);
        if (_selected != null)
        {
            _pathEntry.Text = _selected.RemotePath;
        }
    }

    private void AddSession()
    {
        var s = new SshSession { Name = "New Session", Host = "server.com", Username = "user", RemotePath = "/" };
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
            _syncBtn.Sensitive = true;
            UpdateStatus($"Connected to {_selected.Host}");

            // force default remote path on connect
            _pathEntry.Text = "/";
            _selected.RemotePath = "/";
            SessionStore.Save(_sessions);

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
        _syncBtn.Sensitive = false;
        UpdateStatus("Disconnected");
        _filesStore.Clear();
        _filesCountLabel.Text = "0 items";
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
                _filesStore.AppendValues("go-up-symbolic", "..", "-", "-");
            }

            var count = 0;
            foreach (var f in list)
            {
                if (f.Name == ".") continue;
                if (f.Name == "..") continue;

                var icon = f.IsDirectory ? "folder-symbolic" : "text-x-generic-symbolic";
                var size = f.IsDirectory ? "-" : f.Length.ToString();
                var mod = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                _filesStore.AppendValues(icon, f.Name, size, mod);
                count++;
            }
            _filesCountLabel.Text = $"{count} items";
        }
        catch (Exception ex)
        {
            Toast("Refresh failed: " + ex.Message);
        }
    }

    private void SyncPathFromTerminal()
    {
        if (_sftp == null || !_sftp.IsConnected || _selected == null) return;

        try
        {
            var pwd = GetRemotePwd(_selected);
            if (!string.IsNullOrWhiteSpace(pwd))
            {
                _pathEntry.Text = pwd.Trim();
                RefreshFiles();
            }
        }
        catch (Exception ex)
        {
            Toast("Sync failed: " + ex.Message);
        }
    }

    private bool TryGetSelectedName(out string name, out TreeIter iter)
    {
        name = string.Empty;
        iter = TreeIter.Zero;

        if (!_filesView.Selection.GetSelected(out TreeIter sortIter)) return false;
        iter = _filesSort.ConvertIterToChildIter(sortIter);
        name = (string)_filesStore.GetValue(iter, 1);
        return true;
    }

    private void DownloadSelected()
    {
        if (_sftp == null || !_sftp.IsConnected) return;
        if (!TryGetSelectedRemotePath(out var remotePath, out var isDir)) return;

        if (isDir)
        {
            DownloadFolder(remotePath);
        }
        else
        {
            DownloadFile(remotePath);
        }
    }

    private void UploadFileDialog()
    {
        if (_sftp == null || !_sftp.IsConnected) return;
        using var d = new FileChooserDialog("Upload File", this, FileChooserAction.Open,
            "Cancel", ResponseType.Cancel, "Upload", ResponseType.Accept);

        if (d.Run() == (int)ResponseType.Accept)
        {
            UploadFile(d.Filename);
        }
    }

    private void UploadFolderDialog()
    {
        if (_sftp == null || !_sftp.IsConnected) return;
        using var d = new FileChooserDialog("Upload Folder", this, FileChooserAction.SelectFolder,
            "Cancel", ResponseType.Cancel, "Upload", ResponseType.Accept);

        if (d.Run() == (int)ResponseType.Accept)
        {
            UploadFolder(d.Filename);
        }
    }

    private void CopyRemotePath()
    {
        if (!TryGetSelectedRemotePath(out var remotePath, out _)) return;
        var cb = Clipboard.Get(Gdk.Atom.Intern("CLIPBOARD", false));
        cb.Text = remotePath;
    }

    private void PasteUploadFromClipboard()
    {
        if (_sftp == null || !_sftp.IsConnected) return;
        var cb = Clipboard.Get(Gdk.Atom.Intern("CLIPBOARD", false));
        var text = cb.WaitForText();
        if (string.IsNullOrWhiteSpace(text)) return;

        var path = text.Trim();
        if (System.IO.File.Exists(path))
        {
            UploadFile(path);
        }
        else if (System.IO.Directory.Exists(path))
        {
            UploadFolder(path);
        }
        else
        {
            Toast("Clipboard is not a valid local file/folder path.");
        }
    }

    private bool TryGetSelectedRemotePath(out string remotePath, out bool isDir)
    {
        remotePath = string.Empty;
        isDir = false;

        if (!TryGetSelectedName(out var name, out var iter)) return false;
        if (name == "..") return false;

        remotePath = CombineRemote(_pathEntry.Text, name);

        try
        {
            var attrs = _sftp!.GetAttributes(remotePath);
            isDir = attrs.IsDirectory;
        }
        catch
        {
            return false;
        }

        return true;
    }

    private void DownloadFile(string remotePath)
    {
        using var d = new FileChooserDialog("Save File As", this, FileChooserAction.Save,
            "Cancel", ResponseType.Cancel, "Save", ResponseType.Accept);
        d.CurrentName = System.IO.Path.GetFileName(remotePath);

        if (d.Run() != (int)ResponseType.Accept) return;

        using var fs = System.IO.File.Create(d.Filename);
        _sftp!.DownloadFile(remotePath, fs);
    }

    private void RenameSelected()
    {
        if (_sftp == null || !_sftp.IsConnected) return;
        if (!TryGetSelectedRemotePath(out var remotePath, out _)) return;

        var oldName = System.IO.Path.GetFileName(remotePath.TrimEnd('/'));
        using var d = new Dialog("Rename", this, DialogFlags.Modal);
        d.AddButton("Cancel", ResponseType.Cancel);
        d.AddButton("Rename", ResponseType.Ok);

        var entry = new Entry(oldName);
        d.ContentArea.PackStart(entry, true, true, 8);
        d.ShowAll();

        if (d.Run() == (int)ResponseType.Ok)
        {
            var newName = entry.Text.Trim();
            if (!string.IsNullOrWhiteSpace(newName) && newName != oldName)
            {
                var newPath = CombineRemote(ParentPath(remotePath), newName);
                _sftp!.RenameFile(remotePath, newPath);
                RefreshFiles();
            }
        }
    }

    private void DeleteSelected()
    {
        if (_sftp == null || !_sftp.IsConnected) return;
        if (!TryGetSelectedRemotePath(out var remotePath, out var isDir)) return;

        using var md = new MessageDialog(this, DialogFlags.Modal, MessageType.Question, ButtonsType.YesNo,
            $"Delete {(isDir ? "folder" : "file")} '{System.IO.Path.GetFileName(remotePath)}'?");
        if (md.Run() != (int)ResponseType.Yes) return;

        try
        {
            using var ssh = CreateSshClient(_selected!);
            ssh.Connect();
            ssh.RunCommand($"rm -rf {QuoteForShell(remotePath)}");
            ssh.Disconnect();
            RefreshFiles();
        }
        catch (Exception ex)
        {
            Toast("Delete failed: " + ex.Message);
        }
    }

    private void CompressSelected()
    {
        if (_sftp == null || !_sftp.IsConnected) return;
        if (!TryGetSelectedRemotePath(out var remotePath, out _)) return;

        var baseName = System.IO.Path.GetFileName(remotePath.TrimEnd('/'));
        var format = ChooseArchiveFormat();
        if (format == null) return;

        var archiveName = format == "zip" ? $"{baseName}.zip" : $"{baseName}.tar.gz";
        var parent = ParentPath(remotePath);

        try
        {
            using var ssh = CreateSshClient(_selected!);
            ssh.Connect();
            if (format == "zip")
            {
                ssh.RunCommand($"cd {QuoteForShell(parent)} && zip -r {QuoteForShell(archiveName)} {QuoteForShell(baseName)}");
            }
            else
            {
                ssh.RunCommand($"tar -czf {QuoteForShell(CombineRemote(parent, archiveName))} -C {QuoteForShell(parent)} {QuoteForShell(baseName)}");
            }
            ssh.Disconnect();
            RefreshFiles();
        }
        catch (Exception ex)
        {
            Toast("Compress failed: " + ex.Message);
        }
    }

    private void ExtractSelected()
    {
        if (_sftp == null || !_sftp.IsConnected) return;
        if (!TryGetSelectedRemotePath(out var remotePath, out _)) return;

        var file = remotePath.ToLowerInvariant();
        var parent = ParentPath(remotePath);

        try
        {
            using var ssh = CreateSshClient(_selected!);
            ssh.Connect();
            if (file.EndsWith(".zip"))
            {
                ssh.RunCommand($"unzip -o {QuoteForShell(remotePath)} -d {QuoteForShell(parent)}");
            }
            else if (file.EndsWith(".tar.gz") || file.EndsWith(".tgz"))
            {
                ssh.RunCommand($"tar -xzf {QuoteForShell(remotePath)} -C {QuoteForShell(parent)}");
            }
            else if (file.EndsWith(".tar"))
            {
                ssh.RunCommand($"tar -xf {QuoteForShell(remotePath)} -C {QuoteForShell(parent)}");
            }
            else
            {
                Toast("Unsupported archive type.");
            }
            ssh.Disconnect();
            RefreshFiles();
        }
        catch (Exception ex)
        {
            Toast("Extract failed: " + ex.Message);
        }
    }

    private string? ChooseArchiveFormat()
    {
        using var d = new Dialog("Choose archive format", this, DialogFlags.Modal);
        d.AddButton("Cancel", ResponseType.Cancel);
        d.AddButton("ZIP", ResponseType.Ok);
        d.AddButton("TAR.GZ", ResponseType.Accept);

        var resp = (ResponseType)d.Run();
        if (resp == ResponseType.Ok) return "zip";
        if (resp == ResponseType.Accept) return "tar.gz";
        return null;
    }

    private void OpenWithLocal()
    {
        if (_sftp == null || !_sftp.IsConnected) return;
        if (!TryGetSelectedRemotePath(out var remotePath, out var isDir)) return;
        if (isDir)
        {
            Toast("Open With is for files only.");
            return;
        }

        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetFileName(remotePath));
        try
        {
            using (var fs = System.IO.File.Create(tempPath))
            {
                _sftp!.DownloadFile(remotePath, fs);
            }
            Process.Start(new ProcessStartInfo("xdg-open", tempPath) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Toast("Open failed: " + ex.Message);
        }
    }

    private void DownloadFolder(string remotePath)
    {
        if (_selected == null) return;
        using var d = new FileChooserDialog("Select Download Folder", this, FileChooserAction.SelectFolder,
            "Cancel", ResponseType.Cancel, "Choose", ResponseType.Accept);

        if (d.Run() != (int)ResponseType.Accept) return;

        var targetDir = d.Filename;
        var folderName = System.IO.Path.GetFileName(remotePath.TrimEnd('/'));
        var remoteTar = $"/tmp/lrecodex-{Guid.NewGuid():N}.tar.gz";
        var localTar = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{folderName}-{Guid.NewGuid():N}.tar.gz");

        var parent = ParentPath(remotePath);
        var tarCmd = $"tar -czf {QuoteForShell(remoteTar)} -C {QuoteForShell(parent)} {QuoteForShell(folderName)}";

        try
        {
            using var ssh = CreateSshClient(_selected);
            ssh.Connect();
            ssh.RunCommand(tarCmd);
            ssh.Disconnect();

            using (var fs = System.IO.File.Create(localTar))
            {
                _sftp!.DownloadFile(remoteTar, fs);
            }

            RunLocalCommand("tar", $"-xzf {QuoteForShell(localTar)} -C {QuoteForShell(targetDir)}");

            using var sshCleanup = CreateSshClient(_selected);
            sshCleanup.Connect();
            sshCleanup.RunCommand($"rm -f {QuoteForShell(remoteTar)}");
            sshCleanup.Disconnect();
        }
        catch (Exception ex)
        {
            Toast("Download folder failed: " + ex.Message);
        }
        finally
        {
            try { if (System.IO.File.Exists(localTar)) System.IO.File.Delete(localTar); } catch { }
        }
    }

    private void UploadFile(string localPath)
    {
        if (_sftp == null || !_sftp.IsConnected) return;
        var remotePath = CombineRemote(_pathEntry.Text, System.IO.Path.GetFileName(localPath));

        using var fs = System.IO.File.OpenRead(localPath);
        _sftp.UploadFile(fs, remotePath);
        RefreshFiles();
    }

    private void UploadFolder(string localDir)
    {
        if (_sftp == null || !_sftp.IsConnected || _selected == null) return;
        var folderName = System.IO.Path.GetFileName(localDir.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        var localTar = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{folderName}-{Guid.NewGuid():N}.tar.gz");
        var remoteTar = $"/tmp/lrecodex-upload-{Guid.NewGuid():N}.tar.gz";

        try
        {
            var parentDir = System.IO.Path.GetDirectoryName(localDir) ?? ".";
            RunLocalCommand("tar", $"-czf {QuoteForShell(localTar)} -C {QuoteForShell(parentDir)} {QuoteForShell(folderName)}");

            using (var fs = System.IO.File.OpenRead(localTar))
            {
                _sftp.UploadFile(fs, remoteTar);
            }

            using var ssh = CreateSshClient(_selected);
            ssh.Connect();
            ssh.RunCommand($"tar -xzf {QuoteForShell(remoteTar)} -C {QuoteForShell(_pathEntry.Text)}");
            ssh.RunCommand($"rm -f {QuoteForShell(remoteTar)}");
            ssh.Disconnect();

            RefreshFiles();
        }
        catch (Exception ex)
        {
            Toast("Upload folder failed: " + ex.Message);
        }
        finally
        {
            try { if (System.IO.File.Exists(localTar)) System.IO.File.Delete(localTar); } catch { }
        }
    }

    private void RunLocalCommand(string fileName, string args)
    {
        var p = Process.Start(new ProcessStartInfo(fileName, args)
        {
            UseShellExecute = false
        });
        if (p != null)
        {
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                throw new Exception($"{fileName} failed with exit code {p.ExitCode}");
            }
        }
    }

    private void OnFileActivated(RowActivatedArgs args)
    {
        if (_sftp == null || !_sftp.IsConnected) return;
        if (!TryGetSelectedName(out var name, out var iter)) return;
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

    private void OnFilesButtonPress(object o, ButtonPressEventArgs args)
    {
        if (args.Event.Button != 3) return;

        if (_filesView.GetPathAtPos((int)args.Event.X, (int)args.Event.Y, out var path, out _, out _, out _))
        {
            _filesView.Selection.SelectPath(path);
        }

        ShowFilesContextMenu();
    }

    private void ShowFilesContextMenu()
    {
        if (_sftp == null || !_sftp.IsConnected) return;

        var menu = new Menu();

        var downloadItem = new MenuItem("Download");
        downloadItem.Activated += (_, __) => DownloadSelected();

        var renameItem = new MenuItem("Rename...");
        renameItem.Activated += (_, __) => RenameSelected();

        var deleteItem = new MenuItem("Delete");
        deleteItem.Activated += (_, __) => DeleteSelected();

        var compressItem = new MenuItem("Compress...");
        compressItem.Activated += (_, __) => CompressSelected();

        var extractItem = new MenuItem("Extract Here");
        extractItem.Activated += (_, __) => ExtractSelected();

        var uploadFileItem = new MenuItem("Upload File...");
        uploadFileItem.Activated += (_, __) => UploadFileDialog();

        var uploadFolderItem = new MenuItem("Upload Folder...");
        uploadFolderItem.Activated += (_, __) => UploadFolderDialog();

        var copyPathItem = new MenuItem("Copy Remote Path");
        copyPathItem.Activated += (_, __) => CopyRemotePath();

        var openWithItem = new MenuItem("Open With (Local)...");
        openWithItem.Activated += (_, __) => OpenWithLocal();

        var pasteItem = new MenuItem("Paste (Upload)");
        pasteItem.Activated += (_, __) => PasteUploadFromClipboard();

        menu.Append(downloadItem);
        menu.Append(renameItem);
        menu.Append(deleteItem);
        menu.Append(new SeparatorMenuItem());
        menu.Append(compressItem);
        menu.Append(extractItem);
        menu.Append(new SeparatorMenuItem());
        menu.Append(uploadFileItem);
        menu.Append(uploadFolderItem);
        menu.Append(new SeparatorMenuItem());
        menu.Append(copyPathItem);
        menu.Append(openWithItem);
        menu.Append(pasteItem);

        menu.ShowAll();
        menu.Popup();
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
        var keyPart = !string.IsNullOrWhiteSpace(s.PrivateKeyPath) ? $"-i {QuoteForShell(s.PrivateKeyPath)}" : "";
        var passPart = string.Empty;
        if (string.IsNullOrWhiteSpace(s.PrivateKeyPath) && !string.IsNullOrWhiteSpace(s.Password))
        {
            if (HasExecutable("sshpass"))
            {
                passPart = $"sshpass -p {QuoteForShell(s.Password)} ";
            }
            else
            {
                Toast("sshpass not found. Terminal will prompt for password.");
            }
        }

        var cmd = $"exec xterm -into {xid} -fa 'Monospace' -fs 11 -e {passPart}ssh {keyPart} {portPart} {sshTarget}";
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
                    _syncBtn.Sensitive = false;
                    UpdateStatus("Disconnected");
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
                _termProc.WaitForExit(2000);
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

    private SshClient CreateSshClient(SshSession s)
    {
        if (!string.IsNullOrWhiteSpace(s.PrivateKeyPath))
        {
            var keyFile = string.IsNullOrWhiteSpace(s.PrivateKeyPassphrase)
                ? new PrivateKeyFile(s.PrivateKeyPath)
                : new PrivateKeyFile(s.PrivateKeyPath, s.PrivateKeyPassphrase);

            var auth = new PrivateKeyAuthenticationMethod(s.Username, keyFile);
            var conn = new ConnectionInfo(s.Host, s.Port, s.Username, auth);
            return new SshClient(conn);
        }

        if (string.IsNullOrWhiteSpace(s.Password))
        {
            throw new Exception("No password and no private key set for SSH.");
        }

        return new SshClient(s.Host, s.Port, s.Username, s.Password);
    }

    private string GetRemotePwd(SshSession s)
    {
        if (!string.IsNullOrWhiteSpace(s.PrivateKeyPath))
        {
            var keyFile = string.IsNullOrWhiteSpace(s.PrivateKeyPassphrase)
                ? new PrivateKeyFile(s.PrivateKeyPath)
                : new PrivateKeyFile(s.PrivateKeyPath, s.PrivateKeyPassphrase);

            var auth = new PrivateKeyAuthenticationMethod(s.Username, keyFile);
            var conn = new ConnectionInfo(s.Host, s.Port, s.Username, auth);
            using var ssh = new SshClient(conn);
            ssh.Connect();
            var cmd = ssh.RunCommand("pwd");
            ssh.Disconnect();
            return cmd.Result.Trim();
        }

        if (string.IsNullOrWhiteSpace(s.Password))
        {
            throw new Exception("No password and no private key set for SSH.");
        }

        using var client = new SshClient(s.Host, s.Port, s.Username, s.Password);
        client.Connect();
        var result = client.RunCommand("pwd").Result;
        client.Disconnect();
        return result.Trim();
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
        var keyBrowse = new Button("Browse");
        keyBrowse.Clicked += (_, __) =>
        {
            using var fd = new FileChooserDialog("Select Private Key", this, FileChooserAction.Open,
                "Cancel", ResponseType.Cancel, "Select", ResponseType.Accept);
            if (fd.Run() == (int)ResponseType.Accept)
            {
                key.Text = fd.Filename;
            }
        };
        var remote = new Entry(s.RemotePath);

        AttachRow(grid, 0, "Name", name);
        AttachRow(grid, 1, "Host", host);
        AttachRow(grid, 2, "Port", port);
        AttachRow(grid, 3, "Username", user);
        AttachRow(grid, 4, "Password (optional)", pass);
        AttachRow(grid, 5, "Private key path (optional)", WrapWithButton(key, keyBrowse));
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

    private static Widget WrapWithButton(Widget entry, Widget button)
    {
        var box = new Box(Orientation.Horizontal, 6);
        box.PackStart(entry, true, true, 0);
        box.PackStart(button, false, false, 0);
        return box;
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

    private static bool HasExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return false;

        foreach (var p in path.Split(':'))
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var full = System.IO.Path.Combine(p, name);
            if (System.IO.File.Exists(full)) return true;
        }
        return false;
    }

    private static Button MakeIconButton(string iconName, string label)
    {
        var box = new Box(Orientation.Horizontal, 6);
        var img = new Image { IconName = iconName, PixelSize = 16 };
        var lab = new Label(label);
        box.PackStart(img, false, false, 0);
        box.PackStart(lab, false, false, 0);
        return new Button { Child = box };
    }

    private void UpdateStatus(string text)
    {
        _statusLabel.Text = text;
    }

    private void ApplyCss()
    {
        var css = @"
window {
  background: #0b0f14;
}
.sidebar {
  background: #111722;
  border-right: 1px solid #1b2230;
}
.content {
  background: #0b0f14;
}
headerbar {
  background: #0f1520;
  color: #f1f5ff;
  border-bottom: 1px solid #1b2230;
}
headerbar label {
  color: #f1f5ff;
}
.dialog, dialog, messagedialog, filechooserdialog, GtkDialog {
  background: #0f1520;
  color: #f1f5ff;
}
.dialog label, dialog label, messagedialog label, filechooserdialog label {
  color: #e6eefc;
}
.dialog .content-area, dialog .content-area {
  background: #0f1520;
}
.search-entry, .path-entry, entry {
  background: #151c28;
  color: #f1f5ff;
  border-radius: 8px;
  border: 1px solid #2a3346;
  padding: 6px 8px;
}
spinbutton entry {
  background: #151c28;
  color: #f1f5ff;
  border-radius: 8px;
  border: 1px solid #2a3346;
}
spinbutton button {
  background: #182233;
  color: #f1f5ff;
  border: 1px solid #2a3346;
}
entry:focus {
  border-color: #5aa2ff;
  box-shadow: 0 0 0 1px #5aa2ff;
}
button {
  border-radius: 8px;
  background: #182233;
  color: #f1f5ff;
  padding: 6px 10px;
  border: 1px solid #2a3346;
}
button:hover {
  background: #1f2b40;
}
button.suggested-action {
  background: #2f7cf6;
  color: #ffffff;
  border-color: #2f7cf6;
}
button.destructive-action {
  background: #e45353;
  color: #ffffff;
  border-color: #e45353;
}
menu, .menu, menuitem, .menuitem {
  background: #0f1520;
  color: #e9efff;
}
menuitem:hover, menuitem:selected, .menuitem:hover, .menuitem:selected {
  background: #2a3f6b;
  color: #ffffff;
}
treeview {
  background: #0b0f14;
  color: #e9efff;
}
treeview.view:selected,
treeview.view:selected:focus {
  background: #2a3f6b;
  color: #ffffff;
}
treeview.view:hover {
  background: #141c28;
}
treeview header button {
  background: #141b27;
  color: #dce6ff;
  border-color: #263043;
}
treeview header button:hover {
  background: #1a2332;
}
.statusbar {
  padding: 8px 10px;
  border-top: 1px solid #1b2230;
  background: #0f1520;
}
label {
  color: #d7e2ff;
}
";

        var provider = new CssProvider();
        provider.LoadFromData(css);
        StyleContext.AddProviderForScreen(Gdk.Screen.Default, provider, 800);
    }

    private void SetWindowIcon()
    {
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "LrecodexTerm.png");
            if (System.IO.File.Exists(iconPath))
            {
                Icon = new Gdk.Pixbuf(iconPath);
            }
        }
        catch
        {
            // ignore icon errors
        }
    }

    private Image? CreateHeaderIcon()
    {
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "LrecodexTerm.png");
            if (!System.IO.File.Exists(iconPath))
            {
                var altPath = System.IO.Path.Combine(Environment.CurrentDirectory, "LrecodexTerm.png");
                if (System.IO.File.Exists(altPath))
                {
                    iconPath = altPath;
                }
            }

            Image img;
            if (System.IO.File.Exists(iconPath))
            {
                var pixbuf = new Gdk.Pixbuf(iconPath, 24, 24);
                img = new Image(pixbuf);
            }
            else
            {
                img = new Image { IconName = "utilities-terminal-symbolic", PixelSize = 20 };
            }

            img.Halign = Align.Center;
            img.Valign = Align.Center;
            return img;
        }
        catch
        {
            return null;
        }
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
