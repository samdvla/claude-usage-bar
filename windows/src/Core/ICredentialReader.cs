namespace ClaudeUsage.Core;

public record CredResult(string AccessToken, string? Plan);

public interface ICredentialReader
{
    CredResult? Read();
}
