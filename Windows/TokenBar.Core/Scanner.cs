using System.Text.Json;

namespace TokenBar.Core;

// One scanner instance belongs to one serial scan worker. Cache whole files so
// cumulative Codex counters retain their baseline across reporting periods.
public sealed class Scanner(PricingCatalog? catalog = null)
{
    private readonly PricingCatalog prices = catalog ?? PricingCatalog.Bundled;
    private readonly Dictionary<string, (long Modified, long Length, List<Entry> Entries)> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> projects = new(StringComparer.OrdinalIgnoreCase);
    private record Entry(string Key, string Session, string? Cwd, string? Title, string Provider, string Model,
        DateTimeOffset Date, Usage Usage, double Reasoning = 0);

    public SourceStats[] Scan(SourcePaths paths, ReportingRange range) =>
        [ScanLogs("Claude Code", paths.Claude, range, ParseClaude), ScanLogs("Codex", paths.Codex, range, ParseCodex),
         ScanOpenCode(paths.OpenCode, range), ScanLogs("pi", paths.Pi, range, ParsePi)];

    private SourceStats ScanLogs(string name, string root, ReportingRange range, Func<string, IEnumerable<JsonElement>, List<Entry>> parse)
    {
        var stats = new SourceStats(name, range.Labels.Length) { Available = Directory.Exists(root) };
        if (!stats.Available) return stats;
        try
        {
            var files = Directory.EnumerateFiles(root, "*.jsonl", new EnumerationOptions
                { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var live = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in cache.Keys.Where(p => p.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !live.Contains(p)).ToArray()) cache.Remove(stale);
            var dedup = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc < range.Start.UtcDateTime) continue;
                    var stamp = info.LastWriteTimeUtc.Ticks;
                    if (!cache.TryGetValue(file, out var saved) || saved.Modified != stamp || saved.Length != info.Length)
                    {
                        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(stream);
                        saved = (stamp, info.Length, parse(Path.GetFileNameWithoutExtension(file), ReadJsonLines(reader)));
                        cache[file] = saved;
                    }
                    foreach (var entry in saved.Entries.Where(e => e.Date >= range.Start && e.Date < range.End))
                        if (name == "Claude Code") dedup[entry.Key] = entry;
                        else Add(stats, entry, range);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { stats.Error = "Some logs could not be read: " + ex.Message; }
            }
            foreach (var entry in dedup.Values) Add(stats, entry, range);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { stats.Error = "Could not read logs: " + ex.Message; }
        return stats;
    }

    private static IEnumerable<JsonElement> ReadJsonLines(StreamReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            JsonElement value;
            try { using var doc = JsonDocument.Parse(line); value = doc.RootElement.Clone(); }
            catch (JsonException) { continue; } // An actively written last line may be incomplete.
            yield return value;
        }
    }

    private static List<Entry> ParseClaude(string fallback, IEnumerable<JsonElement> lines)
    {
        var result = new List<Entry>();
        foreach (var d in lines)
        {
            var msg = d.Get("message"); var usage = msg.Get("usage"); var model = msg.Get("model").Text();
            if (d.Get("type").Text() != "assistant" || usage.ValueKind != JsonValueKind.Object || model is null or "<synthetic>" || d.Get("timestamp").Date() is not { } date) continue;
            var session = Clean(d.Get("sessionId").Text()) ?? fallback;
            var request = d.Get("requestId").Text() ?? d.Get("uuid").Text() ?? d.Get("timestamp").Text();
            var creation = usage.Get("cache_creation");
            result.Add(new(session + ":" + request + ":" + msg.Get("id").Text(), session, d.Get("cwd").Text(), d.Get("slug").Text(), "anthropic", model, date,
                new(usage.Get("input_tokens").Num(), usage.Get("output_tokens").Num(), usage.Get("cache_read_input_tokens").Num(),
                    creation.ValueKind == JsonValueKind.Object ? creation.Get("ephemeral_5m_input_tokens").Num() : usage.Get("cache_creation_input_tokens").Num(),
                    creation.Get("ephemeral_1h_input_tokens").Num())));
        }
        return result;
    }

    private static List<Entry> ParseCodex(string fallback, IEnumerable<JsonElement> lines)
    {
        var result = new List<Entry>();
        var session = fallback; string? cwd = null, title = null; var model = "unknown"; var previous = default(JsonElement);
        foreach (var d in lines)
        {
            if (d.Get("timestamp").Date() is not { } date) continue;
            var payload = d.Get("payload");
            switch (d.Get("type").Text())
            {
                case "session_meta":
                    session = Clean(payload.Get("session_id").Text()) ?? Clean(payload.Get("id").Text()) ?? session;
                    cwd = Clean(payload.Get("cwd").Text()) ?? cwd; title = Clean(payload.Get("title").Text()) ?? title;
                    continue;
                case "turn_context":
                    model = Clean(payload.Get("model").Text()) ?? model; cwd = Clean(payload.Get("cwd").Text()) ?? cwd;
                    continue;
            }
            var info = payload.Get("info"); var total = info.Get("total_token_usage"); var last = info.Get("last_token_usage");
            if (d.Get("type").Text() != "event_msg" || payload.Get("type").Text() != "token_count" || total.ValueKind != JsonValueKind.Object) continue;
            double Count(string key) => last.ValueKind == JsonValueKind.Object ? last.Get(key).Num() : Math.Max(0, total.Get(key).Num() - previous.Get(key).Num());
            var input = Math.Max(0, Count("input_tokens"));
            var read = Math.Clamp(Count("cached_input_tokens"), 0, input);
            var write = Math.Clamp(Count("cache_write_input_tokens"), 0, input - read);
            var usage = new Usage(input - read - write, Count("output_tokens"), read, write);
            var reasoning = Count("reasoning_output_tokens");
            previous = total;
            result.Add(new("", session, cwd, title, "openai", model, date, usage, reasoning));
        }
        return result;
    }

    private static List<Entry> ParsePi(string fallback, IEnumerable<JsonElement> lines)
    {
        var result = new List<Entry>(); var session = fallback; string? cwd = null, title = null;
        foreach (var d in lines)
        {
            if (d.Get("type").Text() == "session")
            {
                session = Clean(d.Get("id").Text()) ?? session; cwd = Clean(d.Get("cwd").Text()) ?? cwd;
                title = Clean(d.Get("title").Text()) ?? Clean(d.Get("name").Text()) ?? title; continue;
            }
            if (d.Get("type").Text() == "session_info") { title = Clean(d.Get("name").Text()) ?? Clean(d.Get("title").Text()) ?? title; continue; }
            var msg = d.Get("message"); var usage = msg.Get("usage");
            if (d.Get("type").Text() != "message" || msg.Get("role").Text() != "assistant" || usage.ValueKind != JsonValueKind.Object || d.Get("timestamp").Date() is not { } date) continue;
            result.Add(new("", session, cwd, title, msg.Get("provider").Text() ?? "unknown", msg.Get("model").Text() ?? "unknown", date,
                new(usage.Get("input").Num(), usage.Get("output").Num(), usage.Get("cacheRead").Num(), usage.Get("cacheWrite").Num())));
        }
        return result;
    }

    private void Add(SourceStats stats, Entry entry, ReportingRange range)
    {
        var (cost, estimated) = prices.Price(entry.Provider, entry.Model, entry.Usage, entry.Reasoning);
        var model = stats.Name == "Claude Code" ? entry.Model : PricingCatalog.ProviderId(entry.Provider) + "/" + entry.Model;
        stats.Add(model, entry.Usage with { Cost = cost }, estimated, range.Bucket(entry.Date), entry.Session, ResolveProject(entry.Cwd), Clean(entry.Title), entry.Date);
    }

    private string? ResolveProject(string? raw)
    {
        if (Clean(raw) is not { } path) return null;
        if (projects.TryGetValue(path, out var saved)) return saved;
        try
        {
            if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "");
            var cwd = Path.GetFullPath(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            for (var candidate = new DirectoryInfo(cwd); candidate is not null; candidate = candidate.Parent)
                if (Directory.Exists(Path.Combine(candidate.FullName, ".git")) || File.Exists(Path.Combine(candidate.FullName, ".git"))) return projects[raw!] = candidate.FullName;
            return projects[raw!] = cwd;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }
    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private SourceStats ScanOpenCode(string path, ReportingRange range)
    {
        var stats = new SourceStats("OpenCode", range.Labels.Length) { Available = File.Exists(path) };
        if (!stats.Available) return stats;
        try
        {
            using var db = new SqliteDatabase(path);
            var sessions = OpenCodeMetadata(db);
            foreach (var row in db.Query("SELECT id, session_id, time_created, data FROM message WHERE time_created >= ? AND time_created < ?", range.Start.ToUnixTimeMilliseconds(), range.End.ToUnixTimeMilliseconds()))
            {
                JsonElement d;
                try { using var doc = JsonDocument.Parse(row[3] ?? ""); d = doc.RootElement.Clone(); }
                catch (JsonException) { continue; }
                var tokens = d.Get("tokens");
                if (d.Get("role").Text() != "assistant" || tokens.ValueKind != JsonValueKind.Object) continue;
                var session = row[1] ?? row[0] ?? "unknown-message";
                var created = d.Get("time").Get("created").Num();
                var date = DateTimeOffset.FromUnixTimeMilliseconds(created > 0 ? (long)created : long.Parse(row[2]!, System.Globalization.CultureInfo.InvariantCulture));
                var metadata = sessions.GetValueOrDefault(session);
                var reasoning = tokens.Get("reasoning").Num();
                var usage = new Usage(tokens.Get("input").Num(), tokens.Get("output").Num() + reasoning, tokens.Get("cache").Get("read").Num(), tokens.Get("cache").Get("write").Num());
                Add(stats, new("", session, metadata.Cwd, metadata.Title, d.Get("providerID").Text() ?? "unknown", d.Get("modelID").Text() ?? "unknown", date, usage, reasoning), range);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException or DllNotFoundException) { stats.Error = "Could not read OpenCode database: " + ex.Message; }
        return stats;
    }

    private static Dictionary<string, (string? Cwd, string? Title)> OpenCodeMetadata(SqliteDatabase db)
    {
        var projects = new Dictionary<string, string>();
        var columns = db.Columns("project");
        var projectPath = new[] { "worktree", "directory", "path" }.FirstOrDefault(columns.Contains);
        if (columns.Contains("id") && projectPath is not null)
            foreach (var row in db.Query($"SELECT id, \"{projectPath}\" FROM project"))
                if (row[0] is { } id && row[1] is { } cwd && cwd != "/") projects[id] = cwd;
        columns = db.Columns("session");
        var result = new Dictionary<string, (string? Cwd, string? Title)>();
        if (!columns.Contains("id")) return result;
        string Field(params string[] names) => names.FirstOrDefault(columns.Contains) is { } name ? $"\"{name}\"" : "NULL";
        foreach (var row in db.Query($"SELECT id, {Field("directory", "cwd", "path")}, {Field("title", "name", "slug")}, {Field("project_id", "projectID")} FROM session"))
            if (row[0] is { } id) result[id] = (row[3] is { } project ? projects.GetValueOrDefault(project) ?? row[1] : row[1], row[2]);
        return result;
    }
}
