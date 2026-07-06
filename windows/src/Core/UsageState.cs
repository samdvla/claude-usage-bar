namespace ClaudeUsage.Core;

public enum UsageState
{
    Ok,        // usage numbers present (includes 429 = maxed out)
    NoLogin,   // no Claude Code credential found
    Expired,   // 401/403
    Transient, // network error or non-429 missing headers
    NoData,    // provider has no local data yet (Codex: no session logs)
}
