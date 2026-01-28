using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LrecodexTerm.Terminal;
using Microsoft.Win32;
using Renci.SshNet;

namespace LrecodexTerm;

public partial class MainWindow : Window
{
    private readonly ConPtyHost _pty = new();
    private SshSession? _selected;
    private SftpClient? _sftp;

    public ObservableCollection<SshSession> Sessions { get; } = new();
    public ObservableCollection<FileItem> Files { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        SessionsList.ItemsSource = Sessions;
        FilesGrid.ItemsSource = Files;

        ConnectBtn.Click += (_, __) => Connect();
        DisconnectBtn.Click += (_, __) => Disconnect();
        SyncBtn.Click += (_, __) => SyncPath();
        RefreshBtn.Click += (_, __) => RefreshFiles();

        NewSessionBtn.Click += (_, __) => AddSession();
        EditSessionBtn.Click += (_, __) => EditSession();
        DeleteSessionBtn.Click += (_, __) => DeleteSession();

        SessionsList.SelectionChanged += (_, __) => SelectSession();

        TerminalInput.KeyDown += TerminalInputOnKeyDown;
        _pty.Output += text => Dispatcher.Invoke(() => TerminalOutput.AppendText(text));

        LoadSessions();
    }

    private void LoadSessions()
    {
        Sessions.Clear();
        foreach (var s in SessionStore.Load()) Sessions.Add(s);
    }

    private void SaveSessions()
    {
        SessionStore.Save(Sessions.ToList());
    }

    private void SelectSession()
    {
        _selected = SessionsList.SelectedItem as SshSession;
        if (_selected != null)
        {
            RemotePath.Text = _selected.RemotePath;
        }
    }

    private void AddSession()
    {
        var s = new SshSession { Name = "New Session", Host = "server.com", Username = "user", RemotePath = "/" };
        if (SessionDialog.Edit(this, s))
        {
            Sessions.Add(s);
            SaveSessions();
        }
    }

    private void EditSession()
    {
        if (_selected == null) return;
        var copy = new SshSession
        {
            Name = _selected.Name,
            Host = _selected.Host,
            Port = _selected.Port,
            Username = _selected.Username,
            Password = _selected.Password,
            PrivateKeyPath = _selected.PrivateKeyPath,
            PrivateKeyPassphrase = _selected.PrivateKeyPassphrase,
            RemotePath = _selected.RemotePath
        };

        if (SessionDialog.Edit(this, copy))
        {
            _selected.Name = copy.Name;
            _selected.Host = copy.Host;
            _selected.Port = copy.Port;
            _selected.Username = copy.Username;
            _selected.Password = copy.Password;
            _selected.PrivateKeyPath = copy.PrivateKeyPath;
            _selected.PrivateKeyPassphrase = copy.PrivateKeyPassphrase;
            _selected.RemotePath = copy.RemotePath;
            SaveSessions();
            LoadSessions();
        }
    }

    private void DeleteSession()
    {
        if (_selected == null) return;
        if (MessageBox.Show($"Delete session '{_selected.Name}'?", "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        Sessions.Remove(_selected);
        _selected = null;
        SaveSessions();
    }

    private void Connect()
    {
        if (_selected == null)
        {
            MessageBox.Show("Select a session first.");
            return;
        }

        try
        {
            Disconnect();

            _sftp = CreateSftpClient(_selected);
            _sftp.Connect();

            StartTerminal(_selected);
            StatusText.Text = $"Status: Connected to {_selected.Host}";

            if (string.IsNullOrWhiteSpace(RemotePath.Text)) RemotePath.Text = "/";
            RefreshFiles();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Connect failed");
            Disconnect();
        }
    }

    private void Disconnect()
    {
        try
        {
            if (_sftp != null && _sftp.IsConnected)
            {
                _sftp.Disconnect();
                _sftp.Dispose();
            }
        }
        catch { }

        _sftp = null;
        _pty.Stop();
        StatusText.Text = "Status: Disconnected";
        Files.Clear();
    }

    private void RefreshFiles()
    {
        if (_sftp == null || !_sftp.IsConnected) return;

        try
        {
            var path = RemotePath.Text.Trim();
            if (string.IsNullOrWhiteSpace(path)) path = "/";

            Files.Clear();
            if (path != "/")
            {
                Files.Add(new FileItem { Name = "..", IsDirectory = true, Size = "-", Modified = "-" });
            }

            foreach (var f in _sftp.ListDirectory(path))
            {
                if (f.Name == "." || f.Name == "..") continue;
                Files.Add(new FileItem
                {
                    Name = f.Name,
                    IsDirectory = f.IsDirectory,
                    Size = f.IsDirectory ? "-" : f.Length.ToString(),
                    Modified = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Refresh failed");
        }
    }

    private void SyncPath()
    {
        if (_selected == null) return;
        try
        {
            using var ssh = CreateSshClient(_selected);
            ssh.Connect();
            var cmd = ssh.RunCommand("pwd");
            ssh.Disconnect();
            RemotePath.Text = cmd.Result.Trim();
            RefreshFiles();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Sync failed");
        }
    }

    private void StartTerminal(SshSession s)
    {
        var sshTarget = $"{s.Username}@{s.Host}";
        var portPart = s.Port != 22 ? $"-p {s.Port}" : "";
        var keyPart = !string.IsNullOrWhiteSpace(s.PrivateKeyPath) ? $"-i \"{s.PrivateKeyPath}\"" : "";
        var cmd = $"ssh {keyPart} {portPart} {sshTarget}".Trim();

        _pty.Start(cmd, 140, 35);
    }

    private void TerminalInputOnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var text = TerminalInput.Text + "\r\n";
            TerminalInput.Clear();
            _pty.Send(text);
            e.Handled = true;
        }
    }

    private FileItem? SelectedFile => FilesGrid.SelectedItem as FileItem;

    private string CombineRemote(string basePath, string name)
    {
        basePath = string.IsNullOrWhiteSpace(basePath) ? "/" : basePath.Trim();
        if (!basePath.StartsWith("/")) basePath = "/" + basePath;
        return basePath.EndsWith("/") ? basePath + name : basePath + "/" + name;
    }

    private string ParentPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/") return "/";
        path = path.TrimEnd('/');
        var idx = path.LastIndexOf('/');
        return idx <= 0 ? "/" : path.Substring(0, idx);
    }

    private void OpenItem_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFile == null) return;
        if (SelectedFile.Name == "..")
        {
            RemotePath.Text = ParentPath(RemotePath.Text);
            RefreshFiles();
            return;
        }

        if (SelectedFile.IsDirectory)
        {
            RemotePath.Text = CombineRemote(RemotePath.Text, SelectedFile.Name);
            RefreshFiles();
        }
        else
        {
            OpenWithLocal(false);
        }
    }

    private void OpenWithItem_Click(object sender, RoutedEventArgs e)
    {
        OpenWithLocal(true);
    }

    private void OpenWithLocal(bool showPicker)
    {
        if (_sftp == null || SelectedFile == null || SelectedFile.IsDirectory) return;
        var remotePath = CombineRemote(RemotePath.Text, SelectedFile.Name);
        var tempPath = Path.Combine(Path.GetTempPath(), SelectedFile.Name);

        try
        {
            using (var fs = File.Create(tempPath))
            {
                _sftp.DownloadFile(remotePath, fs);
            }

            if (showPicker)
            {
                Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL \"{tempPath}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Open failed");
        }
    }

    private void DownloadItem_Click(object sender, RoutedEventArgs e)
    {
        if (_sftp == null || SelectedFile == null || SelectedFile.Name == "..") return;
        var remotePath = CombineRemote(RemotePath.Text, SelectedFile.Name);

        if (SelectedFile.IsDirectory)
        {
            DownloadFolder(remotePath);
        }
        else
        {
            var dlg = new SaveFileDialog { FileName = SelectedFile.Name };
            if (dlg.ShowDialog() != true) return;
            using var fs = File.Create(dlg.FileName);
            _sftp.DownloadFile(remotePath, fs);
        }
    }

    private void UploadFileItem_Click(object sender, RoutedEventArgs e)
    {
        if (_sftp == null) return;
        var dlg = new OpenFileDialog();
        if (dlg.ShowDialog() != true) return;
        UploadFile(dlg.FileName);
    }

    private void UploadFolderItem_Click(object sender, RoutedEventArgs e)
    {
        if (_sftp == null) return;
        using var dlg = new System.Windows.Forms.FolderBrowserDialog();
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        UploadFolder(dlg.SelectedPath);
    }

    private void RenameItem_Click(object sender, RoutedEventArgs e)
    {
        if (_sftp == null || SelectedFile == null || SelectedFile.Name == "..") return;
        var oldPath = CombineRemote(RemotePath.Text, SelectedFile.Name);
        var newName = InputDialog.Show(this, "Rename", "New name:", SelectedFile.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == SelectedFile.Name) return;
        var newPath = CombineRemote(ParentPath(oldPath), newName.Trim());
        _sftp.RenameFile(oldPath, newPath);
        RefreshFiles();
    }

    private void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || SelectedFile == null || SelectedFile.Name == "..") return;
        if (MessageBox.Show($"Delete '{SelectedFile.Name}'?", "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

        var remotePath = CombineRemote(RemotePath.Text, SelectedFile.Name);
        using var ssh = CreateSshClient(_selected);
        ssh.Connect();
        ssh.RunCommand($"rm -rf \"{remotePath}\"");
        ssh.Disconnect();
        RefreshFiles();
    }

    private void CompressItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || SelectedFile == null || SelectedFile.Name == "..") return;
        var format = InputDialog.Show(this, "Compress", "Format (zip|tar.gz):", "zip");
        if (string.IsNullOrWhiteSpace(format)) return;

        var name = SelectedFile.Name.Trim();
        var parent = RemotePath.Text.Trim();
        var archive = format == "tar.gz" ? $"{name}.tar.gz" : $"{name}.zip";

        using var ssh = CreateSshClient(_selected);
        ssh.Connect();
        if (format == "tar.gz")
        {
            ssh.RunCommand($"tar -czf \"{CombineRemote(parent, archive)}\" -C \"{parent}\" \"{name}\"");
        }
        else
        {
            ssh.RunCommand($"cd \"{parent}\" && zip -r \"{archive}\" \"{name}\"");
        }
        ssh.Disconnect();
        RefreshFiles();
    }

    private void ExtractItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || SelectedFile == null || SelectedFile.Name == "..") return;
        var file = SelectedFile.Name.ToLowerInvariant();
        var remotePath = CombineRemote(RemotePath.Text, SelectedFile.Name);

        using var ssh = CreateSshClient(_selected);
        ssh.Connect();
        if (file.EndsWith(".zip"))
        {
            ssh.RunCommand($"unzip -o \"{remotePath}\" -d \"{RemotePath.Text}\"");
        }
        else if (file.EndsWith(".tar.gz") || file.EndsWith(".tgz"))
        {
            ssh.RunCommand($"tar -xzf \"{remotePath}\" -C \"{RemotePath.Text}\"");
        }
        else if (file.EndsWith(".tar"))
        {
            ssh.RunCommand($"tar -xf \"{remotePath}\" -C \"{RemotePath.Text}\"");
        }
        else
        {
            MessageBox.Show("Unsupported archive type.");
        }
        ssh.Disconnect();
        RefreshFiles();
    }

    private void UploadFile(string localPath)
    {
        if (_sftp == null) return;
        var remotePath = CombineRemote(RemotePath.Text, Path.GetFileName(localPath));
        using var fs = File.OpenRead(localPath);
        _sftp.UploadFile(fs, remotePath);
        RefreshFiles();
    }

    private void UploadFolder(string localDir)
    {
        if (_sftp == null || _selected == null) return;
        var folderName = Path.GetFileName(localDir.TrimEnd(Path.DirectorySeparatorChar));
        var localTar = Path.Combine(Path.GetTempPath(), $"{folderName}-{Guid.NewGuid():N}.tar.gz");
        var remoteTar = $"/tmp/lrecodex-upload-{Guid.NewGuid():N}.tar.gz";

        RunLocalCommand("tar", $"-czf \"{localTar}\" -C \"{Path.GetDirectoryName(localDir) ?? "."}\" \"{folderName}\"");

        using (var fs = File.OpenRead(localTar))
        {
            _sftp.UploadFile(fs, remoteTar);
        }

        using var ssh = CreateSshClient(_selected);
        ssh.Connect();
        ssh.RunCommand($"tar -xzf \"{remoteTar}\" -C \"{RemotePath.Text}\"");
        ssh.RunCommand($"rm -f \"{remoteTar}\"");
        ssh.Disconnect();

        try { File.Delete(localTar); } catch { }
        RefreshFiles();
    }

    private void DownloadFolder(string remotePath)
    {
        if (_selected == null || _sftp == null) return;
        using var dlg = new System.Windows.Forms.FolderBrowserDialog();
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        var folderName = Path.GetFileName(remotePath.TrimEnd('/'));
        var remoteTar = $"/tmp/lrecodex-{Guid.NewGuid():N}.tar.gz";
        var localTar = Path.Combine(Path.GetTempPath(), $"{folderName}-{Guid.NewGuid():N}.tar.gz");

        using (var ssh = CreateSshClient(_selected))
        {
            ssh.Connect();
            ssh.RunCommand($"tar -czf \"{remoteTar}\" -C \"{ParentPath(remotePath)}\" \"{folderName}\"");
            ssh.Disconnect();
        }

        using (var fs = File.Create(localTar))
        {
            _sftp.DownloadFile(remoteTar, fs);
        }

        RunLocalCommand("tar", $"-xzf \"{localTar}\" -C \"{dlg.SelectedPath}\"");

        using (var ssh = CreateSshClient(_selected))
        {
            ssh.Connect();
            ssh.RunCommand($"rm -f \"{remoteTar}\"");
            ssh.Disconnect();
        }

        try { File.Delete(localTar); } catch { }
    }

    private void RunLocalCommand(string file, string args)
    {
        var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true });
        if (p != null)
        {
            p.WaitForExit();
            if (p.ExitCode != 0) throw new Exception($"{file} failed with exit code {p.ExitCode}");
        }
    }

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
            throw new Exception("No password and no private key set.");
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
            throw new Exception("No password and no private key set.");
        }

        return new SshClient(s.Host, s.Port, s.Username, s.Password);
    }
}

internal static class SessionDialog
{
    public static bool Edit(Window owner, SshSession s)
    {
        var d = new Window
        {
            Title = "Edit Session",
            Owner = owner,
            Width = 400,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var grid = new Grid { Margin = new Thickness(12) };
        for (var i = 0; i < 7; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddRow(grid, 0, "Name", new TextBox { Text = s.Name });
        AddRow(grid, 1, "Host", new TextBox { Text = s.Host });
        AddRow(grid, 2, "Port", new TextBox { Text = s.Port.ToString() });
        AddRow(grid, 3, "Username", new TextBox { Text = s.Username });
        AddRow(grid, 4, "Password", new TextBox { Text = s.Password ?? "" });
        AddRow(grid, 5, "Private key path", new TextBox { Text = s.PrivateKeyPath ?? "" });
        AddRow(grid, 6, "Remote path", new TextBox { Text = s.RemotePath });

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(4, 0, 0, 0) };
        var save = new Button { Content = "Save", Margin = new Thickness(4, 0, 0, 0) };
        btns.Children.Add(cancel);
        btns.Children.Add(save);

        var root = new DockPanel();
        DockPanel.SetDock(btns, Dock.Bottom);
        root.Children.Add(btns);
        root.Children.Add(grid);
        d.Content = root;

        cancel.Click += (_, __) => d.DialogResult = false;
        save.Click += (_, __) =>
        {
            var name = ((TextBox)GetRow(grid, 0)).Text.Trim();
            var host = ((TextBox)GetRow(grid, 1)).Text.Trim();
            var portText = ((TextBox)GetRow(grid, 2)).Text.Trim();
            var user = ((TextBox)GetRow(grid, 3)).Text.Trim();
            var pass = ((TextBox)GetRow(grid, 4)).Text;
            var key = ((TextBox)GetRow(grid, 5)).Text.Trim();
            var remote = ((TextBox)GetRow(grid, 6)).Text.Trim();

            if (!int.TryParse(portText, out var port)) port = 22;

            s.Name = name;
            s.Host = host;
            s.Port = port;
            s.Username = user;
            s.Password = string.IsNullOrWhiteSpace(pass) ? null : pass;
            s.PrivateKeyPath = string.IsNullOrWhiteSpace(key) ? null : key;
            s.RemotePath = string.IsNullOrWhiteSpace(remote) ? "/" : remote;

            d.DialogResult = true;
        };

        return d.ShowDialog() == true;
    }

    private static void AddRow(Grid grid, int row, string label, UIElement input)
    {
        var lbl = new TextBlock { Text = label, Margin = new Thickness(0, 4, 8, 4) };
        Grid.SetRow(lbl, row);
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        Grid.SetRow(input, row);
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
    }

    private static UIElement GetRow(Grid grid, int row)
    {
        return grid.Children.Cast<UIElement>().First(e => Grid.GetRow(e) == row && Grid.GetColumn(e) == 1);
    }
}

internal static class InputDialog
{
    public static string? Show(Window owner, string title, string label, string defaultValue)
    {
        var d = new Window
        {
            Title = title,
            Owner = owner,
            Width = 360,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var stack = new StackPanel { Margin = new Thickness(12) };
        stack.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8) });
        var box = new TextBox { Text = defaultValue };
        stack.Children.Add(box);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(4, 0, 0, 0) };
        var ok = new Button { Content = "OK", Margin = new Thickness(4, 0, 0, 0) };
        btns.Children.Add(cancel);
        btns.Children.Add(ok);
        stack.Children.Add(btns);

        d.Content = stack;

        cancel.Click += (_, __) => d.DialogResult = false;
        ok.Click += (_, __) => d.DialogResult = true;

        return d.ShowDialog() == true ? box.Text : null;
    }
}
