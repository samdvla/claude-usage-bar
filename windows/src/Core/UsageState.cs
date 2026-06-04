namespace ClaudeUsage.Core;

public enum UsageState
{
    Ok,        // headers present (includes 429 = maxed out)
    NoLogin,   // no Claude Code credential found
    Expired,   // 401/403
    Transient, // network error or non-429 missing headers
}
