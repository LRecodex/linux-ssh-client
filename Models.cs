using System;

public class SshSession
{
    public string Name { get; set; } = "New Session";
    public string Host { get; set; } = "example.com";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "user";

    // Auth
    public string? Password { get; set; } = null;
    public string? PrivateKeyPath { get; set; } = null;
    public string? PrivateKeyPassphrase { get; set; } = null;

    // File browser start path
    public string RemotePath { get; set; } = "/home";
}
