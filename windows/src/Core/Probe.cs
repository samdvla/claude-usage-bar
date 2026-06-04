using System.Net;
using System.Text;
using System.Text.Json;

namespace ClaudeUsage.Core;

// Fires the same Claude-Code-shaped probe the macOS app uses and reads the
// anthropic-ratelimit-unified-* headers off the response (including a 429).
public sealed class Probe
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private readonly HttpClient _http;
    private readonly ICredentialReader _creds;

    public Probe(HttpClient http, ICredentialReader creds)
    {
        _http = http;
        _creds = creds;
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    // Convenience ctor for production use.
    public Probe() : this(new HttpClient(), new FileCredentialReader()) { }

    public async Task<RateUsage> FetchAsync(CancellationToken ct = default)
    {
        var cred = _creds.Read();
        if (cred is null || string.IsNullOrEmpty(cred.AccessToken))
            return RateUsage.NoLogin();

        var body = JsonSerializer.Serialize(new
        {
            model = "claude-haiku-4-5",
            max_tokens = 1,
            messages = new[] { new { role = "user", content = "x" } },
            system = new[] { new {
                type = "text",
                text = "You are Claude Code, Anthropic's official CLI for Claude." } },
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {cred.AccessToken}");
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch
        {
            return RateUsage.Transient();
        }

        using (resp)
        {
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return RateUsage.Expired();

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in resp.Headers)
                headers[h.Key] = string.Join(",", h.Value);

            // Missing util headers on a non-429 => transient (handled by parser).
            return RateHeaderParser.Parse(headers, cred.Plan);
        }
    }
}
