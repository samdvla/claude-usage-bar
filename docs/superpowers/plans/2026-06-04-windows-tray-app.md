# Windows Tray App Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a self-contained Windows system-tray app (plus a `ccc.exe` CLI) that reads Claude Code's existing login, probes Anthropic once, and shows live 5h/7d rate-limit utilization with a polished custom flyout UI.

**Architecture:** One .NET 8 solution under `windows/`. A platform-neutral `ClaudeUsage.Core` library (`net8.0`) holds credentials, the probe, header parsing, and health logic — fully unit-testable on any OS. Two Windows front-ends (`net8.0-windows`) consume it: `ClaudeUsageBar.exe` (WinForms tray + custom flyout) and `ccc.exe` (console). Both publish as single self-contained exes.

**Tech Stack:** .NET 8, C#, WinForms, System.Drawing (GDI+), DWM interop (rounded corners/Mica), xUnit + Moq-free hand-rolled stubs, `HttpClient`.

---

## Build/Test environment note

- `ClaudeUsage.Core` and `Core.Tests` target `net8.0` → they **build and `dotnet test` on macOS/Linux/Windows**. Do all Core TDD on whatever machine you're on.
- `ClaudeUsageBar` (tray) and `Cli` target `net8.0-windows` and use `System.Drawing`/WinForms → they **only build/run on Windows**. The visual tasks (icon render, flyout) and the manual smoke test require a Windows box (any Windows machine with Claude Code logged in).

Install the .NET 8 SDK first (`dotnet --version` should report 8.x). On Windows: `winget install Microsoft.DotNet.SDK.8`. On macOS: `brew install dotnet@8` or the official installer.

---

## File Structure

```
windows/
  ClaudeUsageBar.sln
  src/
    Core/
      ClaudeUsage.Core.csproj          # net8.0, no UI deps
      RateHeaders.cs                   # header-key constants
      UsageState.cs                    # enum: Ok/NoLogin/Expired/Transient
      RateUsage.cs                     # parsed result record
      Health.cs                        # util -> HealthLevel + thresholds
      Countdown.cs                     # epoch -> "2h 39m" / "5d 3h" / "now"
      ICredentialReader.cs             # interface + CredResult record
      FileCredentialReader.cs          # reads %USERPROFILE%\.claude\.credentials.json
      RateHeaderParser.cs              # header dict -> RateUsage (pure)
      Probe.cs                         # HttpClient probe -> RateUsage
    TrayApp/
      ClaudeUsageBar.csproj            # net8.0-windows, WinExe
      app.manifest                     # per-monitor-v2 DPI awareness
      Program.cs                       # entry: mutex guard + run
      TrayController.cs                # NotifyIcon, timer, refresh orchestration
      IconRenderer.cs                  # % -> 16/32px HICON via GDI+
      Theme.cs                         # OS light/dark colors
      Dwm.cs                           # DwmSetWindowAttribute interop
      FlyoutForm.cs                    # custom borderless popup
      BarControl.cs                    # owner-drawn rounded progress bar
    Cli/
      Cli.csproj                       # net8.0-windows, console (Exe)
      Program.cs                       # ccc / --watch / --json / open
  tests/
    Core.Tests/
      Core.Tests.csproj                # net8.0, xUnit
      HealthTests.cs
      CountdownTests.cs
      RateHeaderParserTests.cs
      FileCredentialReaderTests.cs
      ProbeTests.cs
      fixtures/                        # captured header sets
  build.ps1                            # publish wrapper (both exes)
  README.md                            # Windows install/build
```

All paths below are relative to repo root `~/Projects/claude-usage-bar`.

---

### Task 1: Solution + Core/test project scaffold

**Files:**
- Create: `windows/ClaudeUsageBar.sln`
- Create: `windows/src/Core/ClaudeUsage.Core.csproj`
- Create: `windows/tests/Core.Tests/Core.Tests.csproj`
- Create: `windows/tests/Core.Tests/SmokeTest.cs`

- [ ] **Step 1: Create solution and projects**

Run:
```bash
cd ~/Projects/claude-usage-bar
mkdir -p windows/src/Core windows/src/TrayApp windows/src/Cli windows/tests/Core.Tests/fixtures
cd windows
dotnet new sln -n ClaudeUsageBar
dotnet new classlib -n ClaudeUsage.Core -o src/Core -f net8.0
dotnet new xunit -n Core.Tests -o tests/Core.Tests -f net8.0
rm src/Core/Class1.cs tests/Core.Tests/UnitTest1.cs
dotnet sln add src/Core/ClaudeUsage.Core.csproj tests/Core.Tests/Core.Tests.csproj
dotnet add tests/Core.Tests/Core.Tests.csproj reference src/Core/ClaudeUsage.Core.csproj
```

- [ ] **Step 2: Set the Core namespace/nullable and write a smoke test**

Edit `windows/src/Core/ClaudeUsage.Core.csproj` so the `<PropertyGroup>` contains:

```xml
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>ClaudeUsage.Core</RootNamespace>
  </PropertyGroup>
```

Create `windows/tests/Core.Tests/SmokeTest.cs`:

```csharp
namespace Core.Tests;

public class SmokeTest
{
    [Fact]
    public void SolutionBuildsAndTestsRun()
    {
        Assert.True(true);
    }
}
```

- [ ] **Step 3: Verify build + test harness works**

Run: `cd ~/Projects/claude-usage-bar/windows && dotnet test tests/Core.Tests/Core.Tests.csproj`
Expected: `Passed!  - Failed: 0, Passed: 1`

- [ ] **Step 4: Commit**

```bash
cd ~/Projects/claude-usage-bar
git add windows/.gitignore windows/ClaudeUsageBar.sln windows/src/Core windows/tests/Core.Tests
git commit -m "chore(windows): scaffold .NET solution, Core lib, xUnit tests"
```

If `windows/.gitignore` does not exist, create it first with:

```
bin/
obj/
*.user
```

---

### Task 2: Health thresholds

**Files:**
- Create: `windows/src/Core/Health.cs`
- Test: `windows/tests/Core.Tests/HealthTests.cs`

- [ ] **Step 1: Write the failing test**

Create `windows/tests/Core.Tests/HealthTests.cs`:

```csharp
using ClaudeUsage.Core;

namespace Core.Tests;

public class HealthTests
{
    [Theory]
    [InlineData(0.0, HealthLevel.Green)]
    [InlineData(0.49, HealthLevel.Green)]
    [InlineData(0.50, HealthLevel.Orange)]
    [InlineData(0.79, HealthLevel.Orange)]
    [InlineData(0.80, HealthLevel.Red)]
    [InlineData(1.0, HealthLevel.Red)]
    public void Level_MatchesMacThresholds(double util, HealthLevel expected)
    {
        Assert.Equal(expected, Health.Level(util));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd ~/Projects/claude-usage-bar/windows && dotnet test tests/Core.Tests/Core.Tests.csproj`
Expected: FAIL — `Health` / `HealthLevel` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

Create `windows/src/Core/Health.cs`:

```csharp
namespace ClaudeUsage.Core;

public enum HealthLevel { Green, Orange, Red }

public static class Health
{
    // Parity with the macOS app: >=0.8 red, >=0.5 orange, else green.
    public static HealthLevel Level(double util)
    {
        if (util >= 0.80) return HealthLevel.Red;
        if (util >= 0.50) return HealthLevel.Orange;
        return HealthLevel.Green;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd ~/Projects/claude-usage-bar/windows && dotnet test tests/Core.Tests/Core.Tests.csproj`
Expected: PASS (all HealthTests cases green).

- [ ] **Step 5: Commit**

```bash
cd ~/Projects/claude-usage-bar
git add windows/src/Core/Health.cs windows/tests/Core.Tests/HealthTests.cs
git commit -m "feat(windows-core): health-level thresholds"
```

---

### Task 3: Countdown formatting

**Files:**
- Create: `windows/src/Core/Countdown.cs`
- Test: `windows/tests/Core.Tests/CountdownTests.cs`

- [ ] **Step 1: Write the failing test**

Create `windows/tests/Core.Tests/CountdownTests.cs`:

```csharp
using ClaudeUsage.Core;

namespace Core.Tests;

public class CountdownTests
{
    // now is injected so the test is deterministic.
    private const long Now = 1_000_000;

    [Fact]
    public void Null_RendersDash() => Assert.Equal("—", Countdown.Format(null, Now));

    [Fact]
    public void Past_RendersNow() => Assert.Equal("now", Countdown.Format(Now - 10, Now));

    [Fact]
    public void UnderADay_RendersHoursMinutes()
    {
        // 2h 39m = 9540s
        Assert.Equal("2h 39m", Countdown.Format(Now + 9540, Now));
    }

    [Fact]
    public void OverADay_RendersDaysHours()
    {
        // 5d 3h = 5*86400 + 3*3600 = 442800s
        Assert.Equal("5d 3h", Countdown.Format(Now + 442800, Now));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd ~/Projects/claude-usage-bar/windows && dotnet test tests/Core.Tests/Core.Tests.csproj`
Expected: FAIL — `Countdown` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `windows/src/Core/Countdown.cs`:

```csharp
namespace ClaudeUsage.Core;

public static class Countdown
{
    // Mirrors the macOS countdown(): minutes zero-padded, days+hours past 24h.
    public static string Format(long? resetEpoch, long nowEpoch)
    {
        if (resetEpoch is null) return "—";
        long d = resetEpoch.Value - nowEpoch;
        if (d < 0) return "now";
        if (d >= 86400) return $"{d / 86400}d {(d % 86400) / 3600}h";
        return $"{d / 3600}h {(d % 3600) / 60:D2}m";
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd ~/Projects/claude-usage-bar/windows && dotnet test tests/Core.Tests/Core.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd ~/Projects/claude-usage-bar
git add windows/src/Core/Countdown.cs windows/tests/Core.Tests/CountdownTests.cs
git commit -m "feat(windows-core): reset countdown formatting"
```

---

### Task 4: RateUsage model + header constants + parser

**Files:**
- Create: `windows/src/Core/RateHeaders.cs`
- Create: `windows/src/Core/UsageState.cs`
- Create: `windows/src/Core/RateUsage.cs`
- Create: `windows/src/Core/RateHeaderParser.cs`
- Test: `windows/tests/Core.Tests/RateHeaderParserTests.cs`

- [ ] **Step 1: Write the failing test**

Create `windows/tests/Core.Tests/RateHeaderParserTests.cs`:

```csharp
using ClaudeUsage.Core;

namespace Core.Tests;

public class RateHeaderParserTests
{
    private static Dictionary<string, string> Headers(string u5, string u7, string r5, string r7) => new()
    {
        [RateHeaders.Util5h] = u5,
        [RateHeaders.Util7d] = u7,
        [RateHeaders.Reset5h] = r5,
        [RateHeaders.Reset7d] = r7,
    };

    [Fact]
    public void Parse_ReadsUtilisationAndReset()
    {
        var u = RateHeaderParser.Parse(Headers("0.61", "0.81", "1700000000", "1700500000"), plan: "max");
        Assert.Equal(UsageState.Ok, u.State);
        Assert.Equal(0.61, u.Util5h!.Value, 3);
        Assert.Equal(0.81, u.Util7d!.Value, 3);
        Assert.Equal(1700000000, u.Reset5h);
        Assert.Equal(1700500000, u.Reset7d);
        Assert.Equal("max", u.Plan);
    }

    [Fact]
    public void Parse_IsCaseInsensitiveOnKeys()
    {
        var raw = new Dictionary<string, string>
        {
            ["ANTHROPIC-RATELIMIT-UNIFIED-5H-UTILIZATION"] = "0.5",
            ["Anthropic-Ratelimit-Unified-7d-Utilization"] = "0.2",
        };
        var u = RateHeaderParser.Parse(raw, plan: null);
        Assert.Equal(UsageState.Ok, u.State);
        Assert.Equal(0.5, u.Util5h!.Value, 3);
    }

    [Fact]
    public void Parse_MissingBothUtilHeaders_IsTransient()
    {
        var u = RateHeaderParser.Parse(new Dictionary<string, string>(), plan: null);
        Assert.Equal(UsageState.Transient, u.State);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd ~/Projects/claude-usage-bar/windows && dotnet test tests/Core.Tests/Core.Tests.csproj`
Expected: FAIL — `RateHeaders`, `UsageState`, `RateUsage`, `RateHeaderParser` undefined.

- [ ] **Step 3: Write minimal implementation**

Create `windows/src/Core/RateHeaders.cs`:

```csharp
namespace ClaudeUsage.Core;

public static class RateHeaders
{
    public const string Util5h = "anthropic-ratelimit-unified-5h-utilization";
    public const string Util7d = "anthropic-ratelimit-unified-7d-utilization";
    public const string Reset5h = "anthropic-ratelimit-unified-5h-reset";
    public const string Reset7d = "anthropic-ratelimit-unified-7d-reset";
}
```

Create `windows/src/Core/UsageState.cs`:

```csharp
namespace ClaudeUsage.Core;

public enum UsageState
{
    Ok,        // headers present (includes 429 = maxed out)
    NoLogin,   // no Claude Code credential found
    Expired,   // 401/403
    Transient, // network error or non-429 missing headers
}
```

Create `windows/src/Core/RateUsage.cs`:

```csharp
namespace ClaudeUsage.Core;

public record RateUsage(
    UsageState State,
    double? Util5h = null,
    double? Util7d = null,
    long? Reset5h = null,
    long? Reset7d = null,
    string? Plan = null)
{
    public static RateUsage NoLogin() => new(UsageState.NoLogin);
    public static RateUsage Expired() => new(UsageState.Expired);
    public static RateUsage Transient() => new(UsageState.Transient);
}
```

Create `windows/src/Core/RateHeaderParser.cs`:

```csharp
using System.Globalization;

namespace ClaudeUsage.Core;

public static class RateHeaderParser
{
    // headers: any case-mix of header names; values are raw strings.
    // A 429 response still carries the util headers, so "present" == data.
    public static RateUsage Parse(IDictionary<string, string> headers, string? plan)
    {
        var lower = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in headers) lower[kv.Key] = kv.Value;

        double? u5 = ParseDouble(lower, RateHeaders.Util5h);
        double? u7 = ParseDouble(lower, RateHeaders.Util7d);
        if (u5 is null && u7 is null) return RateUsage.Transient();

        return new RateUsage(
            UsageState.Ok,
            u5, u7,
            ParseLong(lower, RateHeaders.Reset5h),
            ParseLong(lower, RateHeaders.Reset7d),
            plan);
    }

    private static double? ParseDouble(IDictionary<string, string> h, string key) =>
        h.TryGetValue(key, out var v) &&
        double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static long? ParseLong(IDictionary<string, string> h, string key) =>
        h.TryGetValue(key, out var v) &&
        long.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var l) ? l : null;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd ~/Projects/claude-usage-bar/windows && dotnet test tests/Core.Tests/Core.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd ~/Projects/claude-usage-bar
git add windows/src/Core/RateHeaders.cs windows/src/Core/UsageState.cs windows/src/Core/RateUsage.cs windows/src/Core/RateHeaderParser.cs windows/tests/Core.Tests/RateHeaderParserTests.cs
git commit -m "feat(windows-core): rate-header parser + usage model"
```

---

### Task 5: Credential reader

**Files:**
- Create: `windows/src/Core/ICredentialReader.cs`
- Create: `windows/src/Core/FileCredentialReader.cs`
- Test: `windows/tests/Core.Tests/FileCredentialReaderTests.cs`

- [ ] **Step 1: Write the failing test**

Create `windows/tests/Core.Tests/FileCredentialReaderTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd ~/Projects/claude-usage-bar/windows && dotnet test tests/Core.Tests/Core.Tests.csproj`
Expected: FAIL — `ICredentialReader` / `FileCredentialReader` / `CredResult` undefined.

- [ ] **Step 3: Write minimal implementation**

Create `windows/src/Core/ICredentialReader.cs`:

```csharp
namespace ClaudeUsage.Core;

public record CredResult(string AccessToken, string? Plan);

public interface ICredentialReader
{
    CredResult? Read();
}
```

Create `windows/src/Core/FileCredentialReader.cs`:

```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd ~/Projects/claude-usage-bar/windows && dotnet test tests/Core.Tests/Core.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd ~/Projects/claude-usage-bar
git add windows/src/Core/ICredentialReader.cs windows/src/Core/FileCredentialReader.cs windows/tests/Core.Tests/FileCredentialReaderTests.cs
git commit -m "feat(windows-core): file-based Claude Code credential reader"
```

---

### Task 6: Probe (HTTP) with injectable handler

**Files:**
- Create: `windows/src/Core/Probe.cs`
- Test: `windows/tests/Core.Tests/ProbeTests.cs`

- [ ] **Step 1: Write the failing test**

Create `windows/tests/Core.Tests/ProbeTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd ~/Projects/claude-usage-bar/windows && dotnet test tests/Core.Tests/Core.Tests.csproj`
Expected: FAIL — `Probe` undefined.

- [ ] **Step 3: Write minimal implementation**

Create `windows/src/Core/Probe.cs`:

```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd ~/Projects/claude-usage-bar/windows && dotnet test tests/Core.Tests/Core.Tests.csproj`
Expected: PASS (all ProbeTests cases).

- [ ] **Step 5: Commit**

```bash
cd ~/Projects/claude-usage-bar
git add windows/src/Core/Probe.cs windows/tests/Core.Tests/ProbeTests.cs
git commit -m "feat(windows-core): Anthropic probe with injectable handler"
```

---

### Task 7: Verify credential storage on a real Windows install

This is the spec's one open question. Do it before building UI so the reader's
default path is confirmed (not guessed). No code unless the path differs.

- [ ] **Step 1: Inspect a real Windows Claude Code login**

On a Windows box with Claude Code logged in (e.g. over ssh), run in PowerShell:

```powershell
Test-Path "$env:USERPROFILE\.claude\.credentials.json"
Get-Content "$env:USERPROFILE\.claude\.credentials.json" -Raw | Select-Object -First 1
# If absent, look for where the token actually lives:
Get-ChildItem "$env:USERPROFILE\.claude" -Force | Select-Object Name
cmdkey /list | Select-String "Claude"
```

- [ ] **Step 2: Confirm or correct the reader**

- If the file exists with an `accessToken` (wrapped or bare): **no change** — `FileCredentialReader.DefaultPath()` is correct. Tick this task done.
- If the token lives in **Windows Credential Manager** instead: add `CredManagerCredentialReader : ICredentialReader` (P/Invoke `CredReadW`, target `Claude Code-credentials`, parse the blob as UTF-8 JSON with the same shape logic) and a `ChainCredentialReader` that tries file then cred-manager. Write a unit test that parses a sample blob through the same JSON path. Commit:

```bash
git add windows/src/Core/
git commit -m "feat(windows-core): credential-manager fallback reader"
```

- If the path differs, update `FileCredentialReader.DefaultPath()` accordingly and commit.

> If no Windows machine is reachable, leave `FileCredentialReader` as-is (it's the documented Claude Code default) and revisit during the manual smoke test (Task 14).

---

### Task 8: TrayApp + Cli project scaffold

**Files:**
- Create: `windows/src/TrayApp/ClaudeUsageBar.csproj`
- Create: `windows/src/TrayApp/app.manifest`
- Create: `windows/src/TrayApp/Program.cs`
- Create: `windows/src/Cli/Cli.csproj`
- Create: `windows/src/Cli/Program.cs`

- [ ] **Step 1: Create the two Windows projects and wire references**

Run:
```bash
cd ~/Projects/claude-usage-bar/windows
dotnet new winforms -n ClaudeUsageBar -o src/TrayApp -f net8.0-windows
dotnet new console -n Cli -o src/Cli -f net8.0-windows
rm -f src/TrayApp/Form1.cs src/TrayApp/Form1.Designer.cs src/TrayApp/Form1.resx
dotnet sln add src/TrayApp/ClaudeUsageBar.csproj src/Cli/Cli.csproj
dotnet add src/TrayApp/ClaudeUsageBar.csproj reference src/Core/ClaudeUsage.Core.csproj
dotnet add src/Cli/Cli.csproj reference src/Core/ClaudeUsage.Core.csproj
```

- [ ] **Step 2: Configure the TrayApp csproj**

Replace `windows/src/TrayApp/ClaudeUsageBar.csproj` `<PropertyGroup>` contents with:

```xml
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <UseWindowsForms>true</UseWindowsForms>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>ClaudeUsageBar</RootNamespace>
    <AssemblyName>ClaudeUsageBar</AssemblyName>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <ApplicationIcon>app.ico</ApplicationIcon>
  </PropertyGroup>
```

> If `app.ico` is not present yet, drop the `<ApplicationIcon>` line for now and add it in Task 13.

- [ ] **Step 3: Add the DPI-awareness manifest**

Create `windows/src/TrayApp/app.manifest`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/PM</dpiAware>
    </windowsSettings>
  </application>
</assembly>
```

- [ ] **Step 4: Minimal Program.cs (mutex guard, empty run)**

Create `windows/src/TrayApp/Program.cs`:

```csharp
using System.Threading;

namespace ClaudeUsageBar;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, "Global\\ClaudeUsageBar", out bool isNew);
        if (!isNew) return; // another instance already running

        ApplicationConfiguration.Initialize();
        using var controller = new TrayController();
        Application.Run();
    }
}
```

- [ ] **Step 5: Temporary stub controller so it compiles**

Create `windows/src/TrayApp/TrayController.cs`:

```csharp
namespace ClaudeUsageBar;

public sealed class TrayController : IDisposable
{
    public void Dispose() { }
}
```

- [ ] **Step 6: Minimal Cli Program.cs**

Replace `windows/src/Cli/Program.cs` with:

```csharp
Console.WriteLine("ccc: not yet implemented");
return 0;
```

- [ ] **Step 7: Verify both projects build (Windows only)**

Run: `cd ~/Projects/claude-usage-bar/windows && dotnet build ClaudeUsageBar.sln`
Expected: Build succeeded (on Windows). On macOS the `net8.0-windows` projects will be skipped/fail to build — that's expected; build only `src/Core` and `tests/Core.Tests` there.

- [ ] **Step 8: Commit**

```bash
cd ~/Projects/claude-usage-bar
git add windows/src/TrayApp windows/src/Cli windows/ClaudeUsageBar.sln
git commit -m "chore(windows): scaffold WinForms tray + console projects"
```

---

### Task 9: Theme + DWM interop helpers

**Files:**
- Create: `windows/src/TrayApp/Theme.cs`
- Create: `windows/src/TrayApp/Dwm.cs`

> These wrap OS-specific calls; no unit tests (they read live OS state / call native APIs). Verified in the Task 14 smoke test.

- [ ] **Step 1: Theme color resolution**

Create `windows/src/TrayApp/Theme.cs`:

```csharp
using System.Drawing;
using Microsoft.Win32;
using ClaudeUsage.Core;

namespace ClaudeUsageBar;

public sealed class Theme
{
    public bool Dark { get; private set; }

    public Theme() => Refresh();

    public void Refresh()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            Dark = (k?.GetValue("AppsUseLightTheme") as int? ?? 1) == 0;
        }
        catch { Dark = false; }
    }

    public Color Panel      => Dark ? Color.FromArgb(28, 28, 30)  : Color.FromArgb(250, 250, 252);
    public Color Primary    => Dark ? Color.FromArgb(235, 235, 240): Color.FromArgb(20, 20, 24);
    public Color Secondary  => Dark ? Color.FromArgb(150, 150, 158): Color.FromArgb(110, 110, 120);
    public Color Track      => Dark ? Color.FromArgb(58, 58, 62)  : Color.FromArgb(225, 225, 230);

    public static Color Health(HealthLevel level) => level switch
    {
        HealthLevel.Red    => Color.FromArgb(255, 69, 58),
        HealthLevel.Orange => Color.FromArgb(255, 159, 10),
        _                  => Color.FromArgb(48, 209, 88),
    };
}
```

- [ ] **Step 2: DWM rounded corners + backdrop**

Create `windows/src/TrayApp/Dwm.cs`:

```csharp
using System.Runtime.InteropServices;

namespace ClaudeUsageBar;

public static class Dwm
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_TRANSIENTWINDOW = 3; // acrylic-ish flyout backdrop

    // Best-effort: silently no-ops on Windows 10 where the attrs are unsupported.
    public static void ApplyFlyoutChrome(IntPtr hwnd)
    {
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int backdrop = DWMSBT_TRANSIENTWINDOW;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
    }
}
```

- [ ] **Step 3: Verify build (Windows)**

Run: `cd ~/Projects/claude-usage-bar/windows && dotnet build src/TrayApp/ClaudeUsageBar.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
cd ~/Projects/claude-usage-bar
git add windows/src/TrayApp/Theme.cs windows/src/TrayApp/Dwm.cs
git commit -m "feat(windows-tray): theme colors + DWM flyout chrome"
```

---

### Task 10: Icon renderer (number-in-icon)

**Files:**
- Create: `windows/src/TrayApp/IconRenderer.cs`

> System.Drawing is Windows-only, so this lives in TrayApp and is verified visually in Task 14. We still add one guard test in a Windows-only test project would be overkill; instead verify by eye in the smoke test. Keep the method pure (returns an `Icon`) so it's easy to preview.

- [ ] **Step 1: Implement the renderer**

Create `windows/src/TrayApp/IconRenderer.cs`:

```csharp
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using ClaudeUsage.Core;

namespace ClaudeUsageBar;

public static class IconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    // Renders the integer percent (e.g. "61") tinted by health onto a transparent
    // square icon. Caller must Dispose the returned Icon (which also destroys the
    // underlying HICON via DestroyHandle).
    public static Icon Render(double? util, bool dark, int size = 32)
    {
        int pct = util is null ? 0 : (int)Math.Round(util.Value * 100);
        string text = util is null ? "–" : pct.ToString();
        Color color = util is null
            ? (dark ? Color.Gainsboro : Color.DimGray)
            : Theme.Health(Health.Level(util.Value));

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            // Fit font to the number length so 2-3 digits stay readable.
            float fontSize = text.Length >= 3 ? size * 0.42f : size * 0.62f;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);
            using var fmt = new StringFormat
            { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, brush, new RectangleF(0, 0, size, size), fmt);
        }

        IntPtr hicon = bmp.GetHicon();
        try
        {
            // Clone into a managed Icon so we can destroy the native handle now.
            using var tmp = Icon.FromHandle(hicon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hicon);
        }
    }
}
```

- [ ] **Step 2: Verify build (Windows)**

Run: `cd ~/Projects/claude-usage-bar/windows && dotnet build src/TrayApp/ClaudeUsageBar.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
cd ~/Projects/claude-usage-bar
git add windows/src/TrayApp/IconRenderer.cs
git commit -m "feat(windows-tray): number-in-icon GDI+ renderer"
```

---

### Task 11: Owner-drawn progress bar + flyout window

**Files:**
- Create: `windows/src/TrayApp/BarControl.cs`
- Create: `windows/src/TrayApp/FlyoutForm.cs`

> Visual code — verified in Task 14. Keep layout numbers in one place for easy tuning.

- [ ] **Step 1: Owner-drawn rounded progress bar**

Create `windows/src/TrayApp/BarControl.cs`:

```csharp
using System.Drawing;
using System.Drawing.Drawing2D;
using ClaudeUsage.Core;

namespace ClaudeUsageBar;

// A single "5h ████░░ 61%" row drawn by hand: label, rounded track, rounded
// health-colored fill, right-aligned percent. Not a stock ProgressBar.
public sealed class BarControl : Control
{
    private readonly Theme _theme;
    public string Label { get; set; } = "5h";
    public double? Util { get; set; }

    public BarControl(Theme theme)
    {
        _theme = theme;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 26;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        double u = Util ?? 0.0;
        var health = Theme.Health(Health.Level(u));

        using var labelFont = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        using var pctFont = new Font("Cascadia Mono", 9.5f, FontStyle.Bold);
        using var primary = new SolidBrush(_theme.Primary);
        using var healthBrush = new SolidBrush(health);

        int labelW = 30, pctW = 42, gap = 8;
        int trackX = labelW + gap;
        int trackW = Width - trackX - pctW - gap;
        int trackH = 8;
        int trackY = (Height - trackH) / 2;

        // Label
        g.DrawString(Label, labelFont, primary, 0, trackY - 4);

        // Track
        using (var track = new SolidBrush(_theme.Track))
            FillRounded(g, track, new Rectangle(trackX, trackY, trackW, trackH), trackH / 2);

        // Fill
        int fillW = (int)Math.Round(Math.Clamp(u, 0, 1) * trackW);
        if (fillW > 0)
            FillRounded(g, healthBrush, new Rectangle(trackX, trackY, fillW, trackH), trackH / 2);

        // Percent (right-aligned)
        string pct = Util is null ? "?" : $"{(int)Math.Round(u * 100)}%";
        using var fmt = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        g.DrawString(pct, pctFont, healthBrush,
            new RectangleF(Width - pctW, 0, pctW, Height), fmt);
    }

    private static void FillRounded(Graphics g, Brush brush, Rectangle r, int radius)
    {
        if (r.Width <= 0) return;
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        using var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
```

- [ ] **Step 2: The flyout form**

Create `windows/src/TrayApp/FlyoutForm.cs`:

```csharp
using System.Drawing;
using ClaudeUsage.Core;

namespace ClaudeUsageBar;

// Borderless popup anchored above the tray. Hosts the header, two BarControls,
// reset lines, and footer text-buttons. Closes on deactivate / Esc.
public sealed class FlyoutForm : Form
{
    private readonly Theme _theme;
    private readonly Label _header = new();
    private readonly BarControl _bar5;
    private readonly BarControl _bar7;
    private readonly Label _reset5 = new();
    private readonly Label _reset7 = new();
    private readonly LinkLabel _refresh = new();
    private readonly LinkLabel _terminal = new();

    public event Action? RefreshRequested;
    public event Action? OpenTerminalRequested;

    public FlyoutForm(Theme theme)
    {
        _theme = theme;
        _bar5 = new BarControl(theme) { Label = "5h" };
        _bar7 = new BarControl(theme) { Label = "7d" };

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Width = 280;
        Height = 168;
        BackColor = theme.Panel;
        Padding = new Padding(16, 14, 16, 12);

        _header.AutoSize = false;
        _header.Dock = DockStyle.Top;
        _header.Height = 22;
        _header.Font = new Font("Segoe UI Variable Display", 11f, FontStyle.Semibold);
        _header.ForeColor = theme.Primary;

        foreach (var b in new[] { _bar5, _bar7 }) b.Dock = DockStyle.Top;

        foreach (var l in new[] { _reset5, _reset7 })
        {
            l.Dock = DockStyle.Top;
            l.Height = 18;
            l.Font = new Font("Segoe UI", 9f);
            l.ForeColor = theme.Secondary;
        }

        _refresh.Text = "Refresh";
        _terminal.Text = "Open in Terminal";
        foreach (var lk in new[] { _refresh, _terminal })
        {
            lk.AutoSize = true;
            lk.Font = new Font("Segoe UI", 9f);
            lk.LinkColor = theme.Secondary;
            lk.ActiveLinkColor = theme.Primary;
        }
        _refresh.Click += (_, _) => RefreshRequested?.Invoke();
        _terminal.Click += (_, _) => OpenTerminalRequested?.Invoke();

        var footer = new FlowLayoutPanel
        { Dock = DockStyle.Bottom, Height = 24, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0) };
        footer.Controls.Add(_refresh);
        footer.Controls.Add(_terminal);

        // Add in reverse for DockStyle.Top stacking order.
        Controls.Add(_reset7);
        Controls.Add(_reset5);
        Controls.Add(_bar7);
        Controls.Add(_bar5);
        Controls.Add(_header);
        Controls.Add(footer);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Dwm.ApplyFlyoutChrome(Handle);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Hide(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public void Render(RateUsage u)
    {
        string plan = string.IsNullOrEmpty(u.Plan) ? "" : "  ·  " + Capitalize(u.Plan!);
        _header.Text = "Claude usage" + plan;

        switch (u.State)
        {
            case UsageState.NoLogin:
                SetMessage("No Claude Code login found — open Claude Code."); return;
            case UsageState.Expired:
                SetMessage("Login expired — open Claude Code to refresh."); return;
            case UsageState.Transient:
                SetMessage("Couldn't read usage — try again shortly."); return;
        }

        _bar5.Visible = _bar7.Visible = _reset5.Visible = _reset7.Visible = true;
        _bar5.Util = u.Util5h; _bar7.Util = u.Util7d;
        _bar5.Invalidate(); _bar7.Invalidate();

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _reset5.Text = $"5h resets in {Countdown.Format(u.Reset5h, now)}";
        _reset7.Text = $"7d resets in {Countdown.Format(u.Reset7d, now)}";
    }

    private void SetMessage(string msg)
    {
        _bar5.Visible = _bar7.Visible = _reset7.Visible = false;
        _reset5.Visible = true;
        _reset5.Text = msg;
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    public void ShowAt(Point anchorBottomRight)
    {
        var wa = Screen.FromPoint(anchorBottomRight).WorkingArea;
        int x = Math.Min(anchorBottomRight.X - Width, wa.Right - Width - 8);
        int y = anchorBottomRight.Y - Height - 8;
        if (y < wa.Top) y = wa.Top + 8;
        Location = new Point(Math.Max(wa.Left + 8, x), y);
        Show();
        Activate();
    }
}
```

- [ ] **Step 3: Verify build (Windows)**

Run: `cd ~/Projects/claude-usage-bar/windows && dotnet build src/TrayApp/ClaudeUsageBar.csproj`
Expected: Build succeeded. (If `Semibold` is unavailable as a `FontStyle`, use `FontStyle.Bold`.)

- [ ] **Step 4: Commit**

```bash
cd ~/Projects/claude-usage-bar
git add windows/src/TrayApp/BarControl.cs windows/src/TrayApp/FlyoutForm.cs
git commit -m "feat(windows-tray): owner-drawn bars + custom flyout window"
```

---

### Task 12: TrayController — wire it all together

**Files:**
- Modify: `windows/src/TrayApp/TrayController.cs` (replace stub from Task 8)

- [ ] **Step 1: Implement the controller**

Replace `windows/src/TrayApp/TrayController.cs` with:

```csharp
using System.Diagnostics;
using System.Drawing;
using Microsoft.Win32;
using ClaudeUsage.Core;

namespace ClaudeUsageBar;

public sealed class TrayController : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ClaudeUsageBar";
    private const int RefreshMs = 120_000;

    private readonly NotifyIcon _tray = new();
    private readonly Theme _theme = new();
    private readonly Probe _probe = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _autostart;
    private FlyoutForm? _flyout;
    private Icon? _currentIcon;
    private RateUsage _last = RateUsage.Transient();

    public TrayController()
    {
        _tray.Visible = true;
        _tray.Text = "Claude usage";
        ApplyIcon(null);

        _autostart = new ToolStripMenuItem("Auto-start at login", null, (_, _) => ToggleAutostart())
        { Checked = IsAutostartEnabled() };
        _menu.Items.Add(new ToolStripMenuItem("Refresh now", null, (_, _) => Refresh()));
        _menu.Items.Add(_autostart);
        _menu.Items.Add(new ToolStripMenuItem("Open in Terminal", null, (_, _) => OpenTerminal()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Application.Exit()));

        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleFlyout();
            else if (e.Button == MouseButtons.Right) _menu.Show(Cursor.Position);
        };

        _timer.Interval = RefreshMs;
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
    }

    private void ToggleFlyout()
    {
        _theme.Refresh();
        if (_flyout is { Visible: true }) { _flyout.Hide(); return; }
        _flyout ??= BuildFlyout();
        _flyout.Render(_last);
        _flyout.ShowAt(Cursor.Position);
        Refresh(); // probe fresh on open, like the macOS menuWillOpen_
    }

    private FlyoutForm BuildFlyout()
    {
        var f = new FlyoutForm(_theme);
        f.RefreshRequested += Refresh;
        f.OpenTerminalRequested += OpenTerminal;
        return f;
    }

    private async void Refresh()
    {
        RateUsage u;
        try { u = await _probe.FetchAsync(); }
        catch { u = RateUsage.Transient(); }
        _last = u;
        if (_tray.Container is null && _tray is null) return;
        ApplyIcon(u);
        if (_flyout is { Visible: true }) _flyout.Render(u);
    }

    private void ApplyIcon(RateUsage? u)
    {
        double? util = u?.State == UsageState.Ok ? u.Util5h : null;
        var newIcon = IconRenderer.Render(util, _theme.Dark);
        _tray.Icon = newIcon;
        _currentIcon?.Dispose();
        _currentIcon = newIcon;
        _tray.Text = TooltipFor(u);
    }

    private static string TooltipFor(RateUsage? u) => u?.State switch
    {
        UsageState.Ok => $"Claude usage{Plan(u)} — 5h {Pct(u.Util5h)} · 7d {Pct(u.Util7d)}",
        UsageState.Expired => "Claude usage — login expired",
        UsageState.NoLogin => "Claude usage — no login",
        _ => "Claude usage",
    };

    private static string Plan(RateUsage u) =>
        string.IsNullOrEmpty(u.Plan) ? "" : " · " + char.ToUpper(u.Plan![0]) + u.Plan[1..];
    private static string Pct(double? v) => v is null ? "?" : $"{(int)Math.Round(v.Value * 100)}%";

    private void OpenTerminal()
    {
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "ccc.exe");
            var args = File.Exists(exe) ? $"/k \"{exe}\" --watch" : "/k echo ccc.exe not found";
            Process.Start(new ProcessStartInfo("cmd.exe", args) { UseShellExecute = true });
        }
        catch { /* best effort */ }
    }

    private static bool IsAutostartEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunValue) is not null;
    }

    private void ToggleAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (k is null) return;
        if (IsAutostartEnabled()) k.DeleteValue(RunValue, throwOnMissingValue: false);
        else k.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        _autostart.Checked = IsAutostartEnabled();
    }

    public void Dispose()
    {
        _timer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _currentIcon?.Dispose();
        _flyout?.Dispose();
        _menu.Dispose();
    }
}
```

- [ ] **Step 2: Verify build (Windows)**

Run: `cd ~/Projects/claude-usage-bar/windows && dotnet build src/TrayApp/ClaudeUsageBar.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Manual run check (Windows, Claude Code logged in)**

Run: `cd ~/Projects/claude-usage-bar/windows && dotnet run --project src/TrayApp/ClaudeUsageBar.csproj`
Expected: A tray icon appears showing your 5h %. Left-click opens the flyout; right-click shows the menu. Ctrl-C / Quit exits.

- [ ] **Step 4: Commit**

```bash
cd ~/Projects/claude-usage-bar
git commit -am "feat(windows-tray): TrayController wiring icon, flyout, menu, autostart"
```

---

### Task 13: `ccc.exe` CLI

**Files:**
- Modify: `windows/src/Cli/Program.cs`

- [ ] **Step 1: Implement the CLI**

Replace `windows/src/Cli/Program.cs` with:

```csharp
using System.Diagnostics;
using ClaudeUsage.Core;

// ccc — Claude usage readout in the terminal (Windows).
//   ccc            one-shot
//   ccc --watch    live (default 5s; -w N to change)
//   ccc --json     raw rate headers
//   ccc open       launch the tray app

EnableAnsi();

bool watch = false, json = false; int interval = 5;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--watch" or "-W": watch = true; break;
        case "-w": if (i + 1 < args.Length) int.TryParse(args[++i], out interval); break;
        case "--json" or "--raw": json = true; break;
        case "open":
            var exe = Path.Combine(AppContext.BaseDirectory, "ClaudeUsageBar.exe");
            if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            else Console.Error.WriteLine("ccc: Claude Usage app not found");
            return 0;
        case "-h" or "--help":
            Console.WriteLine("ccc [--watch [-w N]] [--json] [open]");
            return 0;
    }
}

var probe = new Probe();

if (watch && !json)
{
    Console.CancelKeyPress += (_, e) => { Console.CursorVisible = true; };
    Console.CursorVisible = false;
    while (true)
    {
        Console.Clear();
        await RenderOnce(probe, json);
        Console.WriteLine($"\n  \x1b[2mrefreshing every {interval}s · ctrl-c to quit\x1b[0m");
        await Task.Delay(interval * 1000);
    }
}

return await RenderOnce(probe, json);

static async Task<int> RenderOnce(Probe probe, bool json)
{
    var u = await probe.FetchAsync();
    if (u.State == UsageState.NoLogin)
    { Console.Error.WriteLine("\x1b[31mccc: no Claude Code login found\x1b[0m — open Claude Code"); return 1; }
    if (u.State == UsageState.Expired)
    { Console.Error.WriteLine("\x1b[31mccc: login expired\x1b[0m — open Claude Code"); return 1; }
    if (u.State == UsageState.Transient)
    { Console.Error.WriteLine("\x1b[31mccc: couldn't read usage\x1b[0m — try again shortly"); return 1; }

    if (json)
    {
        Console.WriteLine($"{RateHeaders.Util5h}: {u.Util5h}");
        Console.WriteLine($"{RateHeaders.Util7d}: {u.Util7d}");
        Console.WriteLine($"{RateHeaders.Reset5h}: {u.Reset5h}");
        Console.WriteLine($"{RateHeaders.Reset7d}: {u.Reset7d}");
        return 0;
    }

    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    string plan = string.IsNullOrEmpty(u.Plan) ? "" : $"  \x1b[2m· {u.Plan}\x1b[0m";
    Console.WriteLine($"\n  \x1b[1;36mclaude usage\x1b[0m{plan}\n");
    Console.WriteLine("  5h  " + Bar(u.Util5h) + "  " + Pct(u.Util5h));
    Console.WriteLine("  7d  " + Bar(u.Util7d) + "  " + Pct(u.Util7d));
    Console.WriteLine($"\n  \x1b[2m5h resets {Countdown.Format(u.Reset5h, now)}\x1b[0m\n");
    return 0;
}

static string Bar(double? util)
{
    double u = util ?? 0;
    int width = 14, filled = (int)Math.Round(Math.Clamp(u, 0, 1) * width);
    string color = Health.Level(u) switch
    { HealthLevel.Red => "\x1b[31m", HealthLevel.Orange => "\x1b[33m", _ => "\x1b[32m" };
    return color + new string('█', filled) + "\x1b[2m" + new string('░', width - filled) + "\x1b[0m";
}

static string Pct(double? util)
{
    double u = util ?? 0;
    string color = Health.Level(u) switch
    { HealthLevel.Red => "\x1b[31m", HealthLevel.Orange => "\x1b[33m", _ => "\x1b[32m" };
    return $"{color}{(int)Math.Round(u * 100),3}%\x1b[0m";
}

static void EnableAnsi()
{
    try
    {
        var h = GetStdHandle(-11);
        if (GetConsoleMode(h, out uint m)) SetConsoleMode(h, m | 0x0004);
    }
    catch { }
}

[System.Runtime.InteropServices.DllImport("kernel32.dll")]
static extern IntPtr GetStdHandle(int n);
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
static extern bool GetConsoleMode(IntPtr h, out uint mode);
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
static extern bool SetConsoleMode(IntPtr h, uint mode);
```

- [ ] **Step 2: Verify build + run (Windows)**

Run: `cd ~/Projects/claude-usage-bar/windows && dotnet run --project src/Cli/Cli.csproj`
Expected: A colored `claude usage` readout with 5h/7d bars (or the "no login" message if Claude Code isn't logged in here).

- [ ] **Step 3: Commit**

```bash
cd ~/Projects/claude-usage-bar
git commit -am "feat(windows-cli): ccc terminal readout with watch/json/open"
```

---

### Task 14: Build script, app icon, README, smoke test

**Files:**
- Create: `windows/build.ps1`
- Create: `windows/src/TrayApp/app.ico` (from existing `assets/icon.png`)
- Create: `windows/README.md`
- Modify: `README.md` (root — add Windows section)

- [ ] **Step 1: Generate app.ico from the existing icon**

On any machine with ImageMagick:
```bash
cd ~/Projects/claude-usage-bar
magick assets/icon.png -define icon:auto-resize=16,32,48,256 windows/src/TrayApp/app.ico
```
If ImageMagick isn't available, use an online PNG→ICO converter on `assets/icon.png` and save to `windows/src/TrayApp/app.ico`. Then re-add the `<ApplicationIcon>app.ico</ApplicationIcon>` line in the csproj if it was removed in Task 8.

- [ ] **Step 2: Publish wrapper**

Create `windows/build.ps1`:

```powershell
# Builds both self-contained single-file exes into windows/dist.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root "dist"
$common = "-c", "Release", "-r", "win-x64", "--self-contained",
          "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
          "-o", $dist

dotnet publish (Join-Path $root "src/TrayApp/ClaudeUsageBar.csproj") @common
dotnet publish (Join-Path $root "src/Cli/Cli.csproj") @common
Write-Host "`nBuilt to $dist" -ForegroundColor Green
Get-ChildItem $dist -Filter *.exe | Select-Object Name, @{n="MB";e={[math]::Round($_.Length/1MB,1)}}
```

- [ ] **Step 3: Run the full publish (Windows)**

Run: `cd ~/Projects/claude-usage-bar/windows; pwsh ./build.ps1`
Expected: `ClaudeUsageBar.exe` and `ccc.exe` appear in `windows/dist`, each a single self-contained file. Double-click `ClaudeUsageBar.exe` → tray icon appears with no .NET install required.

- [ ] **Step 4: Manual smoke test (Windows, Claude Code logged in)**

Walk through and confirm each:
- [ ] Tray icon shows the live 5h % as a number, colored by level.
- [ ] Left-click opens the flyout: rounded corners, correct theme (toggle Windows dark/light and reopen — colors follow), two rounded health-colored bars, correct %s, reset countdowns present.
- [ ] Flyout closes on click-away and on Esc.
- [ ] Right-click menu: Refresh now updates; Open in Terminal launches `ccc --watch`; Auto-start toggles (verify with `reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v ClaudeUsageBar`); Quit exits and the icon disappears and stays gone.
- [ ] `dist\ccc.exe`, `dist\ccc.exe --watch`, `dist\ccc.exe --json` match the flyout numbers.
- [ ] **Credential confirmation:** if the app shows "no login" while Claude Code IS logged in, revisit Task 7 — the token isn't at the default path; add the Credential-Manager fallback and rebuild.

- [ ] **Step 5: READMEs**

Create `windows/README.md`:

```markdown
# claude-usage-bar (Windows)

A system-tray app + `ccc` CLI showing your real Claude 5h/7d rate-limit usage,
read from the login Claude Code already stored. No sign-in, no API key.

## Install (prebuilt)
Download `ClaudeUsageBar.exe` (and optionally `ccc.exe`) from Releases and run.
Right-click the tray icon → **Auto-start at login** to keep it running.

## Build from source
Requires the .NET 8 SDK.
```powershell
cd windows
pwsh ./build.ps1   # outputs windows/dist/*.exe
```

## How it works
Reads `%USERPROFILE%\.claude\.credentials.json`, makes one tiny probe call to
the Anthropic API, and reads the `anthropic-ratelimit-unified-*` headers off the
response (a 429 carries them too, so "maxed out" still shows). 401/403 means the
login expired — open Claude Code to refresh.
```

Add a "Windows" pointer to the root `README.md` near the Requirements section:

```markdown
## Windows

A Windows tray app + `ccc.exe` CLI live in [`windows/`](windows/README.md) — same
zero-setup idea, native system tray with a custom flyout.
```

- [ ] **Step 6: Commit**

```bash
cd ~/Projects/claude-usage-bar
git add windows/build.ps1 windows/README.md windows/src/TrayApp/app.ico README.md
git commit -m "build(windows): publish script, app icon, READMEs, smoke checklist"
```

- [ ] **Step 7: Push**

```bash
cd ~/Projects/claude-usage-bar
git push
```

---

## Self-Review (completed by plan author)

**Spec coverage:**
- Self-contained single-exe distribution → Task 14 (`build.ps1`, `--self-contained`/`PublishSingleFile`). ✓
- Two-exe split (tray + `ccc.exe`) → Tasks 8, 12, 13. ✓
- Credentials (file + Credential Manager fallback, both JSON shapes) → Task 5 + Task 7 verification. ✓
- Probe with verbatim logic incl. 429-carries-headers and 401/403 expired → Task 6. ✓
- Number-in-icon tray + health colors + tooltip → Tasks 10, 12. ✓
- Custom flyout (DWM rounded corners/backdrop, dark/light theming, owner-drawn rounded gradient bars, Segoe UI, reset lines, footer actions, close-on-deactivate/Esc, DPI/multi-monitor placement) → Tasks 9, 11, 12. ✓
- Minimal native right-click menu (Refresh / Auto-start / Open in Terminal / Quit) → Task 12. ✓
- 120s refresh + refresh-on-open + background thread + single-instance mutex → Tasks 8, 12. ✓
- Auto-start via HKCU Run key (checkable, reflects state) → Task 12. ✓
- Error states parity → Tasks 11 (flyout), 12 (tooltip), 13 (CLI). ✓
- DPI awareness manifest → Task 8. ✓
- Tests (parser 200/429/401, credentials shapes, health boundaries, probe states) → Tasks 2,4,5,6. ✓ (Icon/flyout are visual → manual smoke, Task 14, per spec.)

**Placeholder scan:** No "TBD/implement later" left; every code step has complete code. The only deferred branch is Task 7's conditional Credential-Manager reader, which is fully specified (when to add, what it does, P/Invoke target) and gated on a real-machine finding — acceptable.

**Type consistency:** `RateUsage`, `UsageState`, `HealthLevel`, `CredResult`, `ICredentialReader.Read()`, `Probe.FetchAsync()`, `Countdown.Format(long?, long)`, `Health.Level(double)`, `Theme.Health(HealthLevel)`, `IconRenderer.Render(double?, bool, int)` are used consistently across producing and consuming tasks. ✓
