namespace TokenBar.Core;

public sealed record SourcePaths(string Claude, string Codex, string OpenCode, string Pi)
{
    public static SourcePaths Discover(string? home = null, Func<string, string?>? environment = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        environment ??= Environment.GetEnvironmentVariable;
        string Base(string variable, string fallback) => environment(variable) is { Length: > 0 } path ? Path.GetFullPath(path) : fallback;
        return new(Path.Combine(Base("CLAUDE_CONFIG_DIR", Path.Combine(home, ".claude")), "projects"),
            Path.Combine(Base("CODEX_HOME", Path.Combine(home, ".codex")), "sessions"),
            Path.Combine(Base("XDG_DATA_HOME", Path.Combine(home, ".local", "share")), "opencode", "opencode.db"),
            Path.Combine(Base("PI_CODING_AGENT_DIR", Path.Combine(home, ".pi", "agent")), "sessions"));
    }
    public IEnumerable<string> WatchDirectories => new[] { Claude, Codex, Path.GetDirectoryName(OpenCode)!, Pi };
}
