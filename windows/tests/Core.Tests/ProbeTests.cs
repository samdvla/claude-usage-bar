using System.Net;
using ClaudeUsage.Core;

namespace Core.Tests;

public class ProbeTests
{
    // Hand-rolled stub handler: returns a fixed status + response headers.
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly (string, string)[] _headers;
        public StubHandler(HttpStatusCode code, params (string, string)[] headers)
        { _code = code; _headers = headers; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(_code) { Content = new StringContent("{}") };
            foreach (var (k, v) in _headers) resp.Headers.TryAddWithoutValidation(k, v);
            return Task.FromResult(resp);
        }
    }

    private sealed class StubCreds : ICredentialReader
    {
        private readonly CredResult? _r;
        public StubCreds(CredResult? r) => _r = r;
        public CredResult? Read() => _r;
    }

    private static Probe Make(HttpStatusCode code, CredResult? creds, params (string, string)[] headers)
        => new(new HttpClient(new StubHandler(code, headers)), new StubCreds(creds));

    [Fact]
    public async Task NoCreds_ReturnsNoLogin()
    {
        var u = await Make(HttpStatusCode.OK, creds: null).FetchAsync();
        Assert.Equal(UsageState.NoLogin, u.State);
    }

    [Fact]
    public async Task Ok_ParsesHeaders()
    {
        var u = await Make(HttpStatusCode.OK, new CredResult("tok", "max"),
            (RateHeaders.Util5h, "0.61"), (RateHeaders.Util7d, "0.81")).FetchAsync();
        Assert.Equal(UsageState.Ok, u.State);
        Assert.Equal(0.61, u.Util5h!.Value, 3);
        Assert.Equal("max", u.Plan);
    }

    [Fact]
    public async Task RateLimited429_StillReadsHeaders()
    {
        var u = await Make((HttpStatusCode)429, new CredResult("tok", null),
            (RateHeaders.Util5h, "1.0"), (RateHeaders.Util7d, "1.0")).FetchAsync();
        Assert.Equal(UsageState.Ok, u.State);
        Assert.Equal(1.0, u.Util5h!.Value, 3);
    }

    [Fact]
    public async Task Unauthorized_ReturnsExpired()
    {
        var u = await Make(HttpStatusCode.Unauthorized, new CredResult("tok", null)).FetchAsync();
        Assert.Equal(UsageState.Expired, u.State);
    }

    [Fact]
    public async Task ServerError_NoHeaders_IsTransient()
    {
        var u = await Make(HttpStatusCode.InternalServerError, new CredResult("tok", null)).FetchAsync();
        Assert.Equal(UsageState.Transient, u.State);
    }
}
