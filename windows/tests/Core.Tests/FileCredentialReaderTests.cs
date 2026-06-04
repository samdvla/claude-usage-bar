using ClaudeUsage.Core;

namespace Core.Tests;

public class FileCredentialReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"creds-{Guid.NewGuid():N}.json");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public void Reads_WrappedShape()
    {
        File.WriteAllText(_path,
            """{"claudeAiOauth":{"accessToken":"tok-123","subscriptionType":"max"}}""");
        var c = new FileCredentialReader(_path).Read();
        Assert.NotNull(c);
        Assert.Equal("tok-123", c!.AccessToken);
        Assert.Equal("max", c.Plan);
    }

    [Fact]
    public void Reads_BareShape()
    {
        File.WriteAllText(_path, """{"accessToken":"tok-bare","subscriptionType":"pro"}""");
        var c = new FileCredentialReader(_path).Read();
        Assert.NotNull(c);
        Assert.Equal("tok-bare", c!.AccessToken);
        Assert.Equal("pro", c.Plan);
    }

    [Fact]
    public void MissingFile_ReturnsNull()
        => Assert.Null(new FileCredentialReader(_path).Read());

    [Fact]
    public void MissingToken_ReturnsNull()
    {
        File.WriteAllText(_path, """{"claudeAiOauth":{"subscriptionType":"max"}}""");
        Assert.Null(new FileCredentialReader(_path).Read());
    }
}
