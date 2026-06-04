using System.Text.Json;

namespace ClaudeUsage.Core;

// Reads Claude Code's plaintext credential file. Default location on Windows:
// %USERPROFILE%\.claude\.credentials.json. Accepts both the {"claudeAiOauth":{...}}
// wrapped shape and a bare {...} object, mirroring the macOS reader.
public sealed class FileCredentialReader : ICredentialReader
{
    private readonly string _path;

    public FileCredentialReader(string? path = null)
        => _path = path ?? DefaultPath();

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", ".credentials.json");

    public CredResult? Read()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            var root = doc.RootElement;
            var obj = root.TryGetProperty("claudeAiOauth", out var wrapped) ? wrapped : root;

            if (!obj.TryGetProperty("accessToken", out var tokEl)) return null;
            var token = tokEl.GetString();
            if (string.IsNullOrEmpty(token)) return null;

            string? plan = obj.TryGetProperty("subscriptionType", out var planEl)
                ? planEl.GetString()
                : null;
            return new CredResult(token, plan);
        }
        catch
        {
            return null;
        }
    }
}
