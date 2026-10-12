using System.Globalization;
using System.Text.Json;
using TokenBar.Core;

// Dependency-free regression runner: all fixtures are local and isolated.
var tests = new List<(string Name, Action Test)>();
var now = DateTimeOffset.Now;
var range = new ReportingRange(UsagePeriod.Day);
var inRange = now.ToString("O");
var old = range.Start.AddHours(-1).ToString("O");
var root = Path.Combine(Path.GetTempPath(), "token-bar-tests-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var serial = 0;
SourcePaths Paths()
{
    var path = Path.Combine(root, (++serial).ToString(CultureInfo.InvariantCulture));
    return SourcePaths.Discover(path, _ => null);
}
void Write(string folder, string name, params object[] records)
{
    Directory.CreateDirectory(folder);
    File.WriteAllLines(Path.Combine(folder, name), records.Select(r => r is string text ? text : JsonSerializer.Serialize(r)));
}
object Claude(int output, string request = "r1", string model = "claude-opus-4-6", string session = "s1", string? timestamp = null, string? cwd = null) => new
{
    type = "assistant", timestamp = timestamp ?? inRange, requestId = request, sessionId = session, cwd,
    message = new { id = "m1", model, usage = new { input_tokens = 100, output_tokens = output, cache_read_input_tokens = 50, cache_creation_input_tokens = 10 } }
};
object Context(string model) => new { type = "turn_context", timestamp = inRange, payload = new { model } };
object Counter(int input, int output, string? timestamp = null, int read = 0, int write = 0, int reasoning = 0, object? last = null) => new
{
    type = "event_msg", timestamp = timestamp ?? inRange,
    payload = new { type = "token_count", info = new { total_token_usage = new { input_tokens = input, output_tokens = output, cached_input_tokens = read, cache_write_input_tokens = write, reasoning_output_tokens = reasoning }, last_token_usage = last } }
};
void Equal(double actual, double expected) { if (Math.Abs(actual - expected) > 1e-8) throw new Exception($"Expected {expected}, got {actual}"); }
void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
void Test(string name, Action test) => tests.Add((name, test));

Test("All missing sources are unavailable", () => Check(new Scanner().Scan(Paths(), range).All(s => !s.Available && s.Total == new Usage())));
Test("Claude keeps final streaming record, isolates sessions, skips garbage", () =>
{
    var p = Paths();
    Write(p.Claude, "a.jsonl", Claude(5), "not json", "{", Claude(50), Claude(25, session: "s2"), Claude(999, request: "old", timestamp: old),
        Claude(999, request: "fake", model: "<synthetic>"));
    var s = new Scanner().Scan(p, range)[0];
    Equal(s.Total.Output, 75); Equal(s.Total.Input, 200); Equal(s.Total.CacheRead, 100); Equal(s.Total.CacheWrite, 20);
    Check(s.Sessions.Count == 2 && s.EstimatedPricing.Count == 0);
});
Test("Claude deduplicates records across files", () =>
{
    var p = Paths(); Write(p.Claude, "a.jsonl", Claude(5)); Write(p.Claude, "b.jsonl", Claude(70));
    Equal(new Scanner().Scan(p, range)[0].Total.Output, 70);
});
Test("Claude cache creation split and exact price", () =>
{
    var p = Paths(); Write(p.Claude, "a.jsonl", new { type = "assistant", timestamp = inRange, message = new { model = "claude-opus-4-6",
        usage = new { input_tokens = 1000, output_tokens = 100, cache_read_input_tokens = 200, cache_creation = new { ephemeral_5m_input_tokens = 300, ephemeral_1h_input_tokens = 400 } } } });
    var s = new Scanner().Scan(p, range)[0];
    Equal(s.Total.CacheWrite5m, 300); Equal(s.Total.CacheWrite1h, 400); Equal(s.Total.Cost, (1000 * 5 + 100 * 25 + 200 * .5 + 700 * 6.25) / 1_000_000);
});
Test("Future and stale files do not contribute", () =>
{
    var p = Paths(); Write(p.Claude, "old.jsonl", Claude(500)); File.SetLastWriteTimeUtc(Path.Combine(p.Claude, "old.jsonl"), range.Start.AddHours(-2).UtcDateTime);
    Write(p.Claude, "future.jsonl", Claude(500, timestamp: range.End.AddHours(1).ToString("O")));
    Equal(new Scanner().Scan(p, range)[0].Total.Output, 0);
});
Test("Codex cumulative baseline survives period boundary", () =>
{
    var p = Paths(); Write(p.Codex, "s.jsonl", Context("gpt-5"), Counter(1000, 100, old), Counter(1500, 150), Counter(1500, 150));
    var s = new Scanner().Scan(p, range)[1]; Equal(s.Total.Input, 500); Equal(s.Total.Output, 50);
});
Test("Codex exact last usage splits cache and reasoning", () =>
{
    var p = Paths(); Write(p.Codex, "s.jsonl", Context("gpt-5"), Counter(9999, 9999, last: new { input_tokens = 1000, cached_input_tokens = 400, cache_write_input_tokens = 100, output_tokens = 50, reasoning_output_tokens = 20 }));
    var s = new Scanner().Scan(p, range)[1]; Equal(s.Total.Input, 500); Equal(s.Total.CacheRead, 400); Equal(s.Total.CacheWrite, 100); Equal(s.Total.Output, 50);
});
Test("Codex clamps overlapping cache counters", () =>
{
    var p = Paths(); Write(p.Codex, "s.jsonl", Context("gpt-5"), Counter(100, 10, read: 200, write: 300));
    var s = new Scanner().Scan(p, range)[1]; Equal(s.Total.Input, 0); Equal(s.Total.CacheRead, 100); Equal(s.Total.CacheWrite, 0);
});
Test("Codex model switches and cumulative resets", () =>
{
    var p = Paths(); Write(p.Codex, "s.jsonl", Context("gpt-5"), Counter(100, 10), Context("gpt-5-mini"), Counter(250, 20), Counter(5, 1), Counter(25, 4));
    var s = new Scanner().Scan(p, range)[1]; Equal(s.PerModel["openai/gpt-5"].Input, 100); Equal(s.PerModel["openai/gpt-5-mini"].Input, 170);
});
Test("pi ignores stored cost and groups metadata", () =>
{
    var p = Paths(); Write(p.Pi, "s.jsonl", new { type = "session", id = "pi-session", name = "Test" },
        new { type = "message", timestamp = inRange, message = new { role = "assistant", provider = "anthropic", model = "claude-opus-4-6",
            usage = new { input = 1000, output = 100, cacheRead = 0, cacheWrite = 0, cost = new { total = 9999 } } } });
    var s = new Scanner().Scan(p, range)[3]; Equal(s.Total.Cost, .0075); Check(s.Sessions["pi-session"].Title == "Test");
});
Test("Cache invalidates changed and deleted logs", () =>
{
    var p = Paths(); var scanner = new Scanner(); Write(p.Claude, "s.jsonl", Claude(10)); Equal(scanner.Scan(p, range)[0].Total.Output, 10);
    Write(p.Claude, "s.jsonl", Claude(1234)); Equal(scanner.Scan(p, range)[0].Total.Output, 1234);
    File.Delete(Path.Combine(p.Claude, "s.jsonl")); Equal(scanner.Scan(p, range)[0].Total.Output, 0);
});
Test("Project attribution reaches Windows Git root", () =>
{
    var p = Paths(); var repo = Path.Combine(root, "repo"); var nested = Path.Combine(repo, "src", "feature");
    Directory.CreateDirectory(nested); File.WriteAllText(Path.Combine(repo, ".git"), "gitdir: elsewhere");
    Write(p.Claude, "s.jsonl", Claude(10, cwd: nested));
    Check(new Scanner().Scan(p, range)[0].Sessions["s1"].ProjectPath == repo);
});
Test("Environment overrides and Windows Unicode paths", () =>
{
    var basePath = Path.Combine(root, "使用 space"); var p = SourcePaths.Discover(root, _ => basePath);
    Check(p.Claude == Path.Combine(basePath, "projects") && p.Codex == Path.Combine(basePath, "sessions") && p.OpenCode == Path.Combine(basePath, "opencode", "opencode.db") && p.Pi == Path.Combine(basePath, "sessions"));
});

var custom = new PricingCatalog("""
{"providers":{"test":{"models":{
 "model":{"input":2,"output":10,"reasoning":20,"cache_read":0.5,"cache_write":3,
 "tiers":[{"input":4,"output":15,"cache_read":1,"cache_write":5,"tier":{"type":"context","size":200000}}]},
 "model-fast":{"input":8,"output":30},"no-cache":{"input":1,"output":2}}}}}
""");
Test("Pricing handles reasoning and cache lanes", () => Equal(custom.Price("test", "model", new(1_000_000, 100, 100, 100), 40).Cost, (4_000_000 + 60 * 15 + 40 * 20 + 100 + 500) / 1_000_000.0));
Test("Context tier boundary is strictly greater", () =>
{
    Equal(custom.Price("test", "model", new(200_000)).Cost, .4); Equal(custom.Price("test", "model", new(200_001)).Cost, .800004);
});
Test("Exact mode wins over fallback", () =>
{
    var price = custom.Price("test", "model-fast", new(Output: 1_000_000)); Equal(price.Cost, 30); Check(!price.Estimated);
});
Test("Namespace and provider qualifiers are estimated", () =>
{
    var price = custom.Price("test-custom/plan", "router/model-fast-custom", new(Output: 1_000_000)); Equal(price.Cost, 30); Check(price.Estimated);
});
Test("Unknown models, unrelated providers, missing cache rates cost zero", () =>
{
    foreach (var price in new[] { custom.Price("test", "unknown", new(100)), custom.Price("elsewhere", "model", new(100)), custom.Price("test", "no-cache", new(CacheRead: 100)) })
        Check(price.Cost == 0 && price.Estimated);
});
Test("Bundled catalog and formatting", () =>
{
    Equal(PricingCatalog.Bundled.Price("anthropic", "claude-opus-4-6", new(Output: 1_000_000)).Cost, 25);
    Check(Format.Money(4.821) == "$4.82" && Format.Tokens(48_300) == "48.3K" && Format.Tokens(1_000_000) == "1.0M");
    Equal(new Usage(100, CacheRead: 800, CacheWrite5m: 50, CacheWrite1h: 50).HitRate, .8);
});
Test("Relative periods and year buckets cross year boundary", () =>
{
    var date = new DateTime(2026, 1, 15, 12, 0, 0); var week = new ReportingRange(UsagePeriod.Week, true, date);
    Check(week.Start.LocalDateTime.Date == new DateTime(2026, 1, 9));
    var year = new ReportingRange(UsagePeriod.Year, true, date);
    Check(year.Start.LocalDateTime.Date == new DateTime(2025, 2, 1));
    Equal(year.Bucket(new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date))), 11);
    Equal(year.Bucket(year.Start.AddSeconds(-1)), -1); Equal(year.Bucket(year.End), -1);
    Check(new ReportingRange(UsagePeriod.Month, false, new DateTime(2024, 2, 20)).Labels.Length == 29);
});
Test("OpenCode SQLite reads WAL, Unicode paths, metadata, reasoning and bundled price", () =>
{
    var p = Paths(); p = p with { OpenCode = Path.Combine(root, "資料 db", "opencode.db") };
    Directory.CreateDirectory(Path.GetDirectoryName(p.OpenCode)!);
    using var db = new SqliteDatabase(p.OpenCode, readOnly: false);
    void Execute(string sql) => _ = db.Query(sql).ToArray();
    Execute("PRAGMA journal_mode=WAL");
    Execute("CREATE TABLE message (id TEXT, session_id TEXT, time_created INTEGER, data TEXT)");
    Execute("CREATE TABLE project (id TEXT, worktree TEXT)"); Execute("CREATE TABLE session (id TEXT, project_id TEXT, directory TEXT, title TEXT)");
    var repo = Path.Combine(root, "opencode-project"); Directory.CreateDirectory(repo);
    Execute($"INSERT INTO project VALUES ('p', '{repo.Replace("'", "''")}')");
    Execute("INSERT INTO session VALUES ('s', 'p', 'unused', 'OpenCode session')");
    var data = JsonSerializer.Serialize(new { role = "assistant", providerID = "anthropic", modelID = "claude-opus-4-6", cost = 999,
        tokens = new { input = 1000, output = 100, reasoning = 50, cache = new { read = 200, write = 100 } } });
    Execute($"INSERT INTO message VALUES ('m', 's', {now.ToUnixTimeMilliseconds()}, '{data}')");
    var s = new Scanner().Scan(p, range)[2]; Check(s.Error is null, s.Error ?? ""); Equal(s.Total.Input, 1000); Equal(s.Total.Output, 150);
    Equal(s.Total.Cost, (5000 + 3750 + 100 + 625) / 1_000_000.0); Check(s.Sessions["s"].Title == "OpenCode session" && s.Sessions["s"].ProjectPath == repo);
});
Test("OpenCode works without optional session tables and never creates missing DB", () =>
{
    var p = Paths(); Directory.CreateDirectory(Path.GetDirectoryName(p.OpenCode)!);
    Check(!new Scanner().Scan(p, range)[2].Available && !File.Exists(p.OpenCode));
    using (var db = new SqliteDatabase(p.OpenCode, false)) _ = db.Query("CREATE TABLE message (id TEXT, session_id TEXT, time_created INTEGER, data TEXT)").ToArray();
    var s = new Scanner().Scan(p, range)[2]; Check(s.Available && s.Error is null);
});
Test("Bad SQLite database reports error", () =>
{
    var p = Paths(); Directory.CreateDirectory(Path.GetDirectoryName(p.OpenCode)!); File.WriteAllText(p.OpenCode, "not a database");
    Check(new Scanner().Scan(p, range)[2].Error is not null);
});

var failed = 0;
try
{
    foreach (var (name, test) in tests)
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"{tests.Count - failed}/{tests.Count} tests passed");
return failed == 0 ? 0 : 1;
