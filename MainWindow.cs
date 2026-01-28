using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Gtk;
using Renci.SshNet;

public class MainWindow : Window
{
    private sealed class TabState
    {
        public SshSession Session { get; }
        public SftpClient? Sftp { get; set; }
        public Entry PathEntry { get; }
        public TreeView FilesView { get; }
        public ListStore FilesStore { get; }
        public TreeModelSort FilesSort { get; }
        public Label FilesCountLabel { get; }
        public Socket TermSocket { get; set; }
        public Process? TermProc { get; set; }
        public Frame TermFrame { get; }
        public Widget Container { get; }

        public TabState(SshSession session, Entry pathEntry, TreeView filesView, ListStore filesStore, TreeModelSort filesSort, Label filesCountLabel, Socket termSocket, Frame termFrame, Widget container)
        {
            Session = session;
            PathEntry = pathEntry;
            FilesView = filesView;
            FilesStore = filesStore;
            FilesSort = filesSort;
            FilesCountLabel = filesCountLabel;
            TermSocket = termSocket;
            TermFrame = termFrame;
            Container = container;
        }
    }

    // Data
    private readonly List<SshSession> _sessions;
    private SshSession? _selected;
    private readonly Dictionary<string, TabState> _tabs = new();

    // UI
    private readonly TreeView _sessionsView;
    private readonly ListStore _sessionsStore;
    private readonly TreeModelFilter _sessionsFilter;
    private readonly Entry _sessionSearch;

    private readonly Notebook _tabsNotebook;
    private readonly Button _connectBtn;
    private readonly Button _disconnectBtn;
    private readonly Button _refreshBtn;
    private readonly Button _syncBtn;
    private readonly Label _statusLabel;

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

        _tabsNotebook = new Notebook();
        _tabsNotebook.ShowTabs = true;
        _tabsNotebook.Scrollable = true;
        _tabsNotebook.TabPos = PositionType.Top;
        right.PackStart(_tabsNotebook, true, true, 0);

        // Status bar
        var statusRow = new Box(Orientation.Horizontal, 6);
        statusRow.StyleContext.AddClass("statusbar");
        _statusLabel = new Label("Disconnected") { Xalign = 0 };
        statusRow.PackStart(_statusLabel, true, true, 0);
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

        _tabsNotebook.SwitchPage += (_, __) => UpdateStatusForCurrentTab();
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
            if (_tabs.TryGetValue(_selected.Name, out var tab))
            {
                var page = _tabsNotebook.PageNum(tab.Container);
                if (page >= 0) _tabsNotebook.Page = page;
            }
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

    private TabState GetOrCreateTab(SshSession session)
    {
        if (_tabs.TryGetValue(session.Name, out var existing))
        {
            var page = _tabsNotebook.PageNum(existing.Container);
            if (page >= 0) _tabsNotebook.Page = page;
            return existing;
        }

        var pathEntry = new Entry { Text = session.RemotePath };
        pathEntry.StyleContext.AddClass("path-entry");
        var pathRow = new Box(Orientation.Horizontal, 6);
        pathRow.PackStart(new Label("Remote:"), false, false, 0);
        pathRow.PackStart(pathEntry, true, true, 0);

        var filesStore = new ListStore(typeof(string), typeof(string), typeof(string), typeof(string));
        var filesSort = new TreeModelSort(filesStore);
        var filesView = new TreeView(filesSort);
        filesView.AddEvents((int)Gdk.EventMask.ButtonPressMask);
        var iconRenderer = new CellRendererPixbuf();
        var nameRenderer = new CellRendererText();
        var nameCol = new TreeViewColumn { Title = "Name" };
        nameCol.PackStart(iconRenderer, false);
        nameCol.PackStart(nameRenderer, true);
        nameCol.AddAttribute(iconRenderer, "icon-name", 0);
        nameCol.AddAttribute(nameRenderer, "text", 1);
        nameCol.SortColumnId = 1;
        nameCol.SortIndicator = true;
        filesView.AppendColumn(nameCol);
        var sizeCol = new TreeViewColumn("Size", new CellRendererText(), "text", 2) { SortColumnId = 2, SortIndicator = true };
        var modCol = new TreeViewColumn("Modified", new CellRendererText(), "text", 3) { SortColumnId = 3, SortIndicator = true };
        filesView.AppendColumn(sizeCol);
        filesView.AppendColumn(modCol);

        var filesScroll = new ScrolledWindow();
        filesScroll.Add(filesView);

        var filesCount = new Label("0 items") { Xalign = 1 };

        var filesBox = new Box(Orientation.Vertical, 6);
        filesBox.PackStart(pathRow, false, false, 0);
        filesBox.PackStart(filesScroll, true, true, 0);
        filesBox.PackStart(filesCount, false, false, 0);

        var termSocket = CreateTerminalSocket();
        var termFrame = new Frame("Terminal");
        termFrame.SetSizeRequest(520, -1);
        termFrame.Add(termSocket);

        var split = new Paned(Orientation.Horizontal);
        split.Pack1(termFrame, resize: true, shrink: false);
        split.Pack2(filesBox, resize: true, shrink: false);
        split.Position = 520;

        var tab = new TabState(session, pathEntry, filesView, filesStore, filesSort, filesCount, termSocket, termFrame, split);

        filesView.RowActivated += (_, __) => OnFileActivated(tab);
        filesView.ButtonPressEvent += (o, args) => OnFilesButtonPress(tab, o, args);
        pathEntry.Activated += (_, __) => RefreshFiles(tab);

        var tabLabel = BuildTabLabel(session.Name);
        _tabsNotebook.AppendPage(split, tabLabel);
        _tabsNotebook.ShowAll();
        _tabsNotebook.Page = _tabsNotebook.NPages - 1;

        _tabs[session.Name] = tab;
        return tab;
    }

    private TabState? GetCurrentTabState()
    {
        var page = _tabsNotebook.Page;
        if (page < 0) return null;
        var widget = _tabsNotebook.GetNthPage(page);
        foreach (var kv in _tabs)
        {
            if (kv.Value.Container == widget) return kv.Value;
        }
        return null;
    }

    private void UpdateStatusForCurrentTab()
    {
        var tab = GetCurrentTabState();
        if (tab == null)
        {
            UpdateStatus("Disconnected");
            _disconnectBtn.Sensitive = false;
            _refreshBtn.Sensitive = false;
            _syncBtn.Sensitive = false;
            _connectBtn.Sensitive = true;
            return;
        }
        if (tab.Sftp != null && tab.Sftp.IsConnected)
        {
            UpdateStatus($"Connected to {tab.Session.Host}");
            _disconnectBtn.Sensitive = true;
            _refreshBtn.Sensitive = true;
            _syncBtn.Sensitive = true;
            _connectBtn.Sensitive = false;
        }
        else
        {
            UpdateStatus("Disconnected");
            _disconnectBtn.Sensitive = false;
            _refreshBtn.Sensitive = false;
            _syncBtn.Sensitive = false;
            _connectBtn.Sensitive = true;
        }
    }

    private Widget BuildTabLabel(string title)
    {
        var box = new Box(Orientation.Horizontal, 6);
        var label = new Label(title);
        label.StyleContext.AddClass("tab-text");
        var close = new Button("✕");
        close.Relief = ReliefStyle.None;
        close.StyleContext.AddClass("tab-close");

        close.Clicked += (_, __) =>
        {
            if (_tabs.TryGetValue(title, out var tab))
            {
                Disconnect(tab);
                _tabsNotebook.RemovePage(_tabsNotebook.PageNum(tab.Container));
                _tabs.Remove(title);
            }
        };

        box.PackStart(label, false, false, 0);
        box.PackStart(close, false, false, 0);
        box.ShowAll();
        return box;
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
            var tab = GetOrCreateTab(_selected);
            tab.Container.ShowAll();
            _tabsNotebook.Page = _tabsNotebook.PageNum(tab.Container);
            while (Application.EventsPending())
            {
                Application.RunIteration();
            }
            Disconnect(tab);

            // 1) Terminal: embed xterm and run ssh (defer until socket is realized)
            ScheduleEmbeddedSshTerminal(tab, attempt: 0);

            // 2) SFTP: connect using SSH.NET
            tab.Sftp = CreateSftpClient(_selected);
            tab.Sftp.Connect();

            _disconnectBtn.Sensitive = true;
            _refreshBtn.Sensitive = true;
            _connectBtn.Sensitive = false;
            _syncBtn.Sensitive = true;
            UpdateStatus($"Connected to {_selected.Host}");

            // force default remote path on connect
            tab.PathEntry.Text = "/";
            _selected.RemotePath = "/";
            SessionStore.Save(_sessions);

            // show files immediately
            RefreshFiles(tab);
        }
        catch (Exception ex)
        {
            Toast("Connect failed: " + ex.Message);
            var tab = GetCurrentTabState();
            if (tab != null) Disconnect(tab);
        }
    }

    private void Disconnect()
    {
        var tab = GetCurrentTabState();
        if (tab == null) return;
        Disconnect(tab);
    }

    private void Disconnect(TabState tab)
    {
        try
        {
            if (tab.Sftp != null)
            {
                if (tab.Sftp.IsConnected) tab.Sftp.Disconnect();
                tab.Sftp.Dispose();
                tab.Sftp = null;
            }
        }
        catch
        {
            // ignore
        }

        StopEmbeddedTerminal(tab);

        _disconnectBtn.Sensitive = false;
        _refreshBtn.Sensitive = false;
        _connectBtn.Sensitive = true;
        _syncBtn.Sensitive = false;
        UpdateStatus("Disconnected");
        tab.FilesStore.Clear();
        tab.FilesCountLabel.Text = "0 items";
    }

    private void RefreshFiles()
    {
        var tab = GetCurrentTabState();
        if (tab == null) return;
        RefreshFiles(tab);
    }

    private void RefreshFiles(TabState tab)
    {
        if (tab.Sftp == null || !tab.Sftp.IsConnected) return;

        try
        {
            var path = tab.PathEntry.Text.Trim();
            if (string.IsNullOrWhiteSpace(path)) path = "/";

            var list = tab.Sftp.ListDirectory(path);

            tab.FilesStore.Clear();
            if (path != "/")
            {
                tab.FilesStore.AppendValues("go-up-symbolic", "..", "-", "-");
            }

            var count = 0;
            foreach (var f in list)
            {
                if (f.Name == ".") continue;
                if (f.Name == "..") continue;

                var icon = f.IsDirectory ? "folder-symbolic" : "text-x-generic-symbolic";
                var size = f.IsDirectory ? "-" : f.Length.ToString();
                var mod = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                tab.FilesStore.AppendValues(icon, f.Name, size, mod);
                count++;
            }
            tab.FilesCountLabel.Text = $"{count} items";
        }
        catch (Exception ex)
        {
            Toast("Refresh failed: " + ex.Message);
        }
    }

    private void SyncPathFromTerminal()
    {
        var tab = GetCurrentTabState();
        if (tab == null || tab.Sftp == null || !tab.Sftp.IsConnected) return;

        try
        {
            var pwd = GetRemotePwd(tab.Session);
            if (!string.IsNullOrWhiteSpace(pwd))
            {
                tab.PathEntry.Text = pwd.Trim();
                RefreshFiles(tab);
            }
        }
        catch (Exception ex)
        {
            Toast("Sync failed: " + ex.Message);
        }
    }

    private bool TryGetSelectedName(TabState tab, out string name, out TreeIter iter)
    {
        name = string.Empty;
        iter = TreeIter.Zero;

        if (!tab.FilesView.Selection.GetSelected(out TreeIter sortIter)) return false;
        iter = tab.FilesSort.ConvertIterToChildIter(sortIter);
        name = (string)tab.FilesStore.GetValue(iter, 1);
        return true;
    }

    private void DownloadSelected(TabState tab)
    {
        if (tab.Sftp == null || !tab.Sftp.IsConnected) return;
        if (!TryGetSelectedRemotePath(tab, out var remotePath, out var isDir)) return;

        if (isDir)
        {
            DownloadFolder(tab, remotePath);
        }
        else
        {
            DownloadFile(tab, remotePath);
        }
    }

    private void UploadFileDialog(TabState tab)
    {
        if (tab.Sftp == null || !tab.Sftp.IsConnected) return;
        using var d = new FileChooserDialog("Upload File", this, FileChooserAction.Open,
            "Cancel", ResponseType.Cancel, "Upload", ResponseType.Accept);

        if (d.Run() == (int)ResponseType.Accept)
        {
            UploadFile(tab, d.Filename);
        }
    }

    private void UploadFolderDialog(TabState tab)
    {
        if (tab.Sftp == null || !tab.Sftp.IsConnected) return;
        using var d = new FileChooserDialog("Upload Folder", this, FileChooserAction.SelectFolder,
            "Cancel", ResponseType.Cancel, "Upload", ResponseType.Accept);

        if (d.Run() == (int)ResponseType.Accept)
        {
            UploadFolder(tab, d.Filename);
        }
    }

    private void CopyRemotePath(TabState tab)
    {
        if (!TryGetSelectedRemotePath(tab, out var remotePath, out _)) return;
        var cb = Clipboard.Get(Gdk.Atom.Intern("CLIPBOARD", false));
        cb.Text = remotePath;
    }

    private void PasteUploadFromClipboard(TabState tab)
    {
        if (tab.Sftp == null || !tab.Sftp.IsConnected) return;
        var cb = Clipboard.Get(Gdk.Atom.Intern("CLIPBOARD", false));
        var text = cb.WaitForText();
        if (string.IsNullOrWhiteSpace(text)) return;

        var path = text.Trim();
        if (System.IO.File.Exists(path))
        {
            UploadFile(tab, path);
        }
        else if (System.IO.Directory.Exists(path))
        {
            UploadFolder(tab, path);
        }
        else
        {
            Toast("Clipboard is not a valid local file/folder path.");
        }
    }

    private bool TryGetSelectedRemotePath(TabState tab, out string remotePath, out bool isDir)
    {
        remotePath = string.Empty;
        isDir = false;

        if (!TryGetSelectedName(tab, out var name, out var iter)) return false;
        if (name == "..") return false;

        remotePath = CombineRemote(tab.PathEntry.Text, name);

        try
        {
            var attrs = tab.Sftp!.GetAttributes(remotePath);
            isDir = attrs.IsDirectory;
        }
        catch
        {
            return false;
        }

        return true;
    }

    private void DownloadFile(TabState tab, string remotePath)
    {
        using var d = new FileChooserDialog("Save File As", this, FileChooserAction.Save,
            "Cancel", ResponseType.Cancel, "Save", ResponseType.Accept);
        d.CurrentName = System.IO.Path.GetFileName(remotePath);

        if (d.Run() != (int)ResponseType.Accept) return;

        using var fs = System.IO.File.Create(d.Filename);
        tab.Sftp!.DownloadFile(remotePath, fs);
    }

    private void RenameSelected(TabState tab)
    {
        if (tab.Sftp == null || !tab.Sftp.IsConnected) return;
        if (!TryGetSelectedRemotePath(tab, out var remotePath, out _)) return;

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
                tab.Sftp!.RenameFile(remotePath, newPath);
                RefreshFiles(tab);
            }
        }
    }

    private void DeleteSelected(TabState tab)
    {
        if (tab.Sftp == null || !tab.Sftp.IsConnected) return;
        if (!TryGetSelectedRemotePath(tab, out var remotePath, out var isDir)) return;

        using var md = new MessageDialog(this, DialogFlags.Modal, MessageType.Question, ButtonsType.YesNo,
            $"Delete {(isDir ? "folder" : "file")} '{System.IO.Path.GetFileName(remotePath)}'?");
        if (md.Run() != (int)ResponseType.Yes) return;

        try
        {
            using var ssh = CreateSshClient(tab.Session);
            ssh.Connect();
            ssh.RunCommand($"rm -rf {QuoteForShell(remotePath)}");
            ssh.Disconnect();
            RefreshFiles(tab);
        }
        catch (Exception ex)
        {
            Toast("Delete failed: " + ex.Message);
        }
    }

    private void CompressSelected(TabState tab)
    {
        if (tab.Sftp == null || !tab.Sftp.IsConnected) return;
        if (!TryGetSelectedRemotePath(tab, out var remotePath, out _)) return;

        var baseName = System.IO.Path.GetFileName(remotePath.TrimEnd('/'));
        var format = ChooseArchiveFormat();
        if (format == null) return;

        var archiveName = format == "zip" ? $"{baseName}.zip" : $"{baseName}.tar.gz";
        var parent = ParentPath(remotePath);

        try
        {
            using var ssh = CreateSshClient(tab.Session);
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
            RefreshFiles(tab);
        }
        catch (Exception ex)
        {
            Toast("Compress failed: " + ex.Message);
        }
    }

    private void ExtractSelected(TabState tab)
    {
        if (tab.Sftp == null || !tab.Sftp.IsConnected) return;
        if (!TryGetSelectedRemotePath(tab, out var remotePath, out _)) return;

        var file = remotePath.ToLowerInvariant();
        var parent = ParentPath(remotePath);

        try
        {
            using var ssh = CreateSshClient(tab.Session);
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
            RefreshFiles(tab);
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

    private void OpenWithLocal(TabState tab)
    {
        if (tab.Sftp == null || !tab.Sftp.IsConnected) return;
        if (!TryGetSelectedRemotePath(tab, out var remotePath, out var isDir)) return;
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
                tab.Sftp!.DownloadFile(remotePath, fs);
            }

            var contentType = GLib.ContentType.Guess(tempPath, null, out _);
            using var dlg = new AppChooserDialog(this, DialogFlags.Modal, contentType);
            dlg.ShowAll();
            if (dlg.Run() != (int)ResponseType.Ok)
            {
                return;
            }

            var appInfo = dlg.AppInfo;
            if (appInfo != null)
            {
                var uri = new Uri(tempPath).AbsoluteUri;
                var list = new GLib.List(IntPtr.Zero);
                list.Append(GLib.Marshaller.StringToPtrGStrdup(uri));
                appInfo.LaunchUris(list, null);
            }
        }
        catch (Exception ex)
        {
            Toast("Open failed: " + ex.Message);
        }
    }

    private void DownloadFolder(TabState tab, string remotePath)
    {
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
            using var ssh = CreateSshClient(tab.Session);
            ssh.Connect();
            ssh.RunCommand(tarCmd);
            ssh.Disconnect();

            using (var fs = System.IO.File.Create(localTar))
            {
                tab.Sftp!.DownloadFile(remoteTar, fs);
            }

            RunLocalCommand("tar", $"-xzf {QuoteForShell(localTar)} -C {QuoteForShell(targetDir)}");

            using var sshCleanup = CreateSshClient(tab.Session);
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

    private void UploadFile(TabState tab, string localPath)
    {
        if (tab.Sftp == null || !tab.Sftp.IsConnected) return;
        var remotePath = CombineRemote(tab.PathEntry.Text, System.IO.Path.GetFileName(localPath));

        using var fs = System.IO.File.OpenRead(localPath);
        tab.Sftp.UploadFile(fs, remotePath);
        RefreshFiles(tab);
    }

    private void UploadFolder(TabState tab, string localDir)
    {
        if (tab.Sftp == null || !tab.Sftp.IsConnected) return;
        var folderName = System.IO.Path.GetFileName(localDir.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        var localTar = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{folderName}-{Guid.NewGuid():N}.tar.gz");
        var remoteTar = $"/tmp/lrecodex-upload-{Guid.NewGuid():N}.tar.gz";

        try
        {
            var parentDir = System.IO.Path.GetDirectoryName(localDir) ?? ".";
            RunLocalCommand("tar", $"-czf {QuoteForShell(localTar)} -C {QuoteForShell(parentDir)} {QuoteForShell(folderName)}");

            using (var fs = System.IO.File.OpenRead(localTar))
            {
                tab.Sftp.UploadFile(fs, remoteTar);
            }

            using var ssh = CreateSshClient(tab.Session);
            ssh.Connect();
            ssh.RunCommand($"tar -xzf {QuoteForShell(remoteTar)} -C {QuoteForShell(tab.PathEntry.Text)}");
            ssh.RunCommand($"rm -f {QuoteForShell(remoteTar)}");
            ssh.Disconnect();

            RefreshFiles(tab);
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

    private void OnFileActivated(TabState tab)
    {
        if (tab.Sftp == null || !tab.Sftp.IsConnected) return;
        if (!TryGetSelectedName(tab, out var name, out var iter)) return;
        if (name == "..")
        {
            tab.PathEntry.Text = ParentPath(tab.PathEntry.Text);
            RefreshFiles(tab);
            return;
        }

        var newPath = CombineRemote(tab.PathEntry.Text, name);

        try
        {
            var attrs = tab.Sftp.GetAttributes(newPath);
            if (attrs.IsDirectory)
            {
                tab.PathEntry.Text = newPath;
                RefreshFiles(tab);
            }
            else
            {
                OpenFileInNano(tab, newPath);
            }
        }
        catch
        {
            // ignore (file or no perm)
        }
    }

    private void OnFilesButtonPress(TabState tab, object o, ButtonPressEventArgs args)
    {
        if (args.Event.Button != 3) return;

        if (tab.FilesView.GetPathAtPos((int)args.Event.X, (int)args.Event.Y, out var path, out _, out _, out _))
        {
            tab.FilesView.Selection.SelectPath(path);
        }

        ShowFilesContextMenu(tab);
    }

    private void ShowFilesContextMenu(TabState tab)
    {
        if (tab.Sftp == null || !tab.Sftp.IsConnected) return;

        var menu = new Menu();

        var downloadItem = new MenuItem("Download");
        downloadItem.Activated += (_, __) => DownloadSelected(tab);

        var renameItem = new MenuItem("Rename...");
        renameItem.Activated += (_, __) => RenameSelected(tab);

        var deleteItem = new MenuItem("Delete");
        deleteItem.Activated += (_, __) => DeleteSelected(tab);

        var compressItem = new MenuItem("Compress...");
        compressItem.Activated += (_, __) => CompressSelected(tab);

        var extractItem = new MenuItem("Extract Here");
        extractItem.Activated += (_, __) => ExtractSelected(tab);

        var uploadFileItem = new MenuItem("Upload File...");
        uploadFileItem.Activated += (_, __) => UploadFileDialog(tab);

        var uploadFolderItem = new MenuItem("Upload Folder...");
        uploadFolderItem.Activated += (_, __) => UploadFolderDialog(tab);

        var copyPathItem = new MenuItem("Copy Remote Path");
        copyPathItem.Activated += (_, __) => CopyRemotePath(tab);

        var openWithItem = new MenuItem("Open With (Local)...");
        openWithItem.Activated += (_, __) => OpenWithLocal(tab);

        var pasteItem = new MenuItem("Paste (Upload)");
        pasteItem.Activated += (_, __) => PasteUploadFromClipboard(tab);

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
    private void ScheduleEmbeddedSshTerminal(TabState tab, int attempt)
    {
        GLib.Idle.Add(() =>
        {
            try
            {
                StartEmbeddedSshTerminal(tab);
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Terminal socket not ready", StringComparison.OrdinalIgnoreCase) && attempt < 5)
                {
                    ScheduleEmbeddedSshTerminal(tab, attempt + 1);
                    return false;
                }
                Toast("Terminal start failed: " + ex.Message);
            }
            return false;
        });
    }

    private void StartEmbeddedSshTerminal(TabState tab)
    {
        tab.Container.ShowAll();
        RecreateTerminalSocket(tab);

        if (!IsRealized)
        {
            ShowAll();
        }

        if (!tab.TermSocket.Visible)
        {
            tab.TermSocket.Show();
        }

        // Let GTK finish realize/map before grabbing the XID
        for (var i = 0; i < 20 && !tab.TermSocket.IsRealized; i++)
        {
            while (Application.EventsPending())
            {
                Application.RunIteration();
            }
        }

        var xid = tab.TermSocket.Id;
        if (xid == 0) throw new Exception("Terminal socket not ready.");

        var s = tab.Session;
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

        var xrm = "XTerm*selectToClipboard: true";
        var xrm2 = "XTerm*VT100*translations: #override Ctrl Shift <Key>V: insert-selection(CLIPBOARD) \\n Ctrl Shift <Key>C: copy-selection(CLIPBOARD)";
        var cmd = $"exec xterm -into {xid} -fa 'Monospace' -fs 11 -xrm {QuoteForShell(xrm)} -xrm {QuoteForShell(xrm2)} -e {passPart}ssh {keyPart} {portPart} {sshTarget}";
        tab.TermProc = Process.Start(new ProcessStartInfo("/bin/bash", $"-lc \"{cmd}\"")
        {
            UseShellExecute = false
        });

        if (tab.TermProc != null)
        {
            tab.TermProc.EnableRaisingEvents = true;
            tab.TermProc.Exited += (_, __) =>
            {
                Application.Invoke((_, __) =>
                {
                    StopEmbeddedTerminal(tab, recreateSocket: true);
                    _disconnectBtn.Sensitive = false;
                    _refreshBtn.Sensitive = false;
                    _connectBtn.Sensitive = true;
                    _syncBtn.Sensitive = false;
                    UpdateStatus("Disconnected");
                });
            };
        }
    }

    private void StopEmbeddedTerminal(TabState tab, bool recreateSocket = false)
    {
        try
        {
            if (tab.TermProc != null && !tab.TermProc.HasExited)
            {
                tab.TermProc.Kill();
                tab.TermProc.WaitForExit(2000);
            }
        }
        catch
        {
            // ignore
        }
        tab.TermProc = null;

        if (recreateSocket)
        {
            RecreateTerminalSocket(tab);
        }
    }

    private void OpenFileInNano(TabState tab, string remotePath)
    {
        var s = tab.Session;
        var sshTarget = $"{s.Username}@{s.Host}";
        var portPart = s.Port != 22 ? $"-p {s.Port}" : "";
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

    private void RecreateTerminalSocket(TabState tab)
    {
        if (tab.TermSocket != null)
        {
            if (tab.TermSocket.Parent == tab.TermFrame)
            {
                tab.TermFrame.Remove(tab.TermSocket);
            }
            tab.TermSocket.Destroy();
        }

        tab.TermSocket = CreateTerminalSocket();
        tab.TermFrame.Add(tab.TermSocket);
        tab.TermFrame.ShowAll();
        tab.Container.ShowAll();
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
.notebook {
  background: #0b0f14;
}
notebook > header.top {
  background: #0f1520;
  padding: 6px 6px 0 6px;
}
notebook > header.top tab {
  background: #141b27;
  border-radius: 8px 8px 0 0;
  padding: 4px 10px;
  border: 1px solid #263043;
  margin-right: 6px;
}
notebook > header.top tab:checked,
notebook > header.top tab:active {
  background: #22314a;
}
.tab-text {
  color: #e9efff;
  font-weight: 600;
}
.tab-close {
  background: transparent;
  border-radius: 6px;
  padding: 0 6px;
  color: #e9efff;
}
.tab-close:hover {
  background: #2a3f6b;
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
        foreach (var tab in _tabs.Values)
        {
            Disconnect(tab);
        }
        Application.Quit();
    }
}
