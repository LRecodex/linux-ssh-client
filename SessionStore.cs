using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

public static class SessionStore
{
    private static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".config", "mintterm");

    private static string SessionsPath => Path.Combine(ConfigDir, "sessions.json");

    public static List<SshSession> Load()
    {
        Directory.CreateDirectory(ConfigDir);

        if (!File.Exists(SessionsPath))
        {
            var seed = new List<SshSession>
            {
                new SshSession { Name = "My Server", Host = "server.com", Username = "user", RemotePath = "/" }
            };
            Save(seed);
            return seed;
        }

        var json = File.ReadAllText(SessionsPath);
        return JsonConvert.DeserializeObject<List<SshSession>>(json) ?? new List<SshSession>();
    }

    public static void Save(List<SshSession> sessions)
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonConvert.SerializeObject(sessions, Formatting.Indented);
        File.WriteAllText(SessionsPath, json);
    }
}
