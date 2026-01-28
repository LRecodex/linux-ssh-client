namespace LrecodexTerm;

public class SshSession
{
    public string Name { get; set; } = "New Session";
    public string Host { get; set; } = "example.com";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "user";

    public string? Password { get; set; } = null;
    public string? PrivateKeyPath { get; set; } = null;
    public string? PrivateKeyPassphrase { get; set; } = null;

    public string RemotePath { get; set; } = "/";
}

public class FileItem
{
    public string Name { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public string Modified { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
}
