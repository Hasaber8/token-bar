using System.Globalization;
using System.Text.Json;

namespace TokenBar.Core;

public record Usage(double Input = 0, double Output = 0, double CacheRead = 0,
    double CacheWrite5m = 0, double CacheWrite1h = 0, double Cost = 0)
{
    public double CacheWrite => CacheWrite5m + CacheWrite1h;
    public double Context => Input + CacheRead + CacheWrite;
    public double HitRate => Context > 0 ? CacheRead / Context : 0;
    public static Usage operator +(Usage a, Usage b) => new(a.Input + b.Input, a.Output + b.Output,
        a.CacheRead + b.CacheRead, a.CacheWrite5m + b.CacheWrite5m, a.CacheWrite1h + b.CacheWrite1h, a.Cost + b.Cost);
}

public enum UsagePeriod { Day, Week, Month, Year }

public sealed class ReportingRange
{
    public UsagePeriod Period { get; }
    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }
    public string[] Labels { get; }
    public ReportingRange(UsagePeriod period, bool relative = false, DateTime? now = null)
    {
        Period = period;
        var today = (now ?? DateTime.Now).Date;
        var month = new DateTime(today.Year, today.Month, 1);
        var firstWeekday = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var start = period switch
        {
            UsagePeriod.Week => relative ? today.AddDays(-6) : today.AddDays(-((7 + (int)today.DayOfWeek - (int)firstWeekday) % 7)),
            UsagePeriod.Month => relative ? today.AddDays(-29) : month,
            UsagePeriod.Year => relative ? month.AddMonths(-11) : new DateTime(today.Year, 1, 1),
            _ => today
        };
        var count = period switch { UsagePeriod.Day => 24, UsagePeriod.Week => 7,
            UsagePeriod.Month => relative ? 30 : DateTime.DaysInMonth(today.Year, today.Month), _ => 12 };
        Start = Local(start);
        End = Local(period switch { UsagePeriod.Day => start.AddDays(1), UsagePeriod.Year => start.AddMonths(12), _ => start.AddDays(count) });
        Labels = Enumerable.Range(0, count).Select(i => period switch
        {
            UsagePeriod.Day => i.ToString(CultureInfo.InvariantCulture),
            UsagePeriod.Year => start.AddMonths(i).ToString("MMM"),
            _ => start.AddDays(i).ToString("MMM d")
        }).ToArray();
    }
    private static DateTimeOffset Local(DateTime value) => new(value, TimeZoneInfo.Local.GetUtcOffset(value));
    public int Bucket(DateTimeOffset value)
    {
        if (value < Start || value >= End) return -1;
        var local = value.LocalDateTime;
        return Period switch
        {
            UsagePeriod.Day => local.Hour,
            UsagePeriod.Year => (local.Year - Start.LocalDateTime.Year) * 12 + local.Month - Start.LocalDateTime.Month,
            _ => (local.Date - Start.LocalDateTime.Date).Days
        };
    }
}

public sealed class SourceStats(string name, int bucketCount)
{
    public string Name { get; } = name;
    public bool Available { get; set; }
    public string? Error { get; set; }
    public Usage Total { get; private set; } = new();
    public Dictionary<string, Usage> PerModel { get; } = new(StringComparer.Ordinal);
    public HashSet<string> EstimatedPricing { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, SessionStats> Sessions { get; } = new(StringComparer.Ordinal);
    public double[] Buckets { get; } = new double[bucketCount];
    public void Add(string model, Usage usage, bool estimated, int bucket, string sessionId, string? cwd, string? title, DateTimeOffset date)
    {
        Total += usage;
        PerModel[model] = PerModel.GetValueOrDefault(model, new Usage()) + usage;
        if (estimated) EstimatedPricing.Add(model);
        if (bucket >= 0 && bucket < Buckets.Length) Buckets[bucket] += usage.Cost;
        if (Sessions.TryGetValue(sessionId, out var session))
            Sessions[sessionId] = session with { Total = session.Total + usage,
                ProjectPath = session.ProjectPath is null || date < session.FirstActivity ? cwd ?? session.ProjectPath : session.ProjectPath,
                Title = session.Title ?? title, FirstActivity = date < session.FirstActivity ? date : session.FirstActivity,
                LastActivity = date > session.LastActivity ? date : session.LastActivity };
        else Sessions[sessionId] = new(sessionId, cwd, title, date, date, usage);
    }
}

public record SessionStats(string Id, string? ProjectPath, string? Title, DateTimeOffset FirstActivity, DateTimeOffset LastActivity, Usage Total);

public static class Format
{
    public static string Money(double value) => "$" + value.ToString("F2", CultureInfo.InvariantCulture);
    public static string Tokens(double n) => n switch
    {
        >= 10_000_000 => (n / 1_000_000).ToString("F0", CultureInfo.InvariantCulture) + "M",
        >= 1_000_000 => (n / 1_000_000).ToString("F1", CultureInfo.InvariantCulture) + "M",
        >= 100_000 => (n / 1_000).ToString("F0", CultureInfo.InvariantCulture) + "K",
        >= 1_000 => (n / 1_000).ToString("F1", CultureInfo.InvariantCulture) + "K",
        _ => n.ToString("F0", CultureInfo.InvariantCulture)
    };
}

internal static class Json
{
    public static JsonElement Get(this JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value) ? value : default;
    public static string? Text(this JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    public static double Num(this JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var n) && double.IsFinite(n) ? n : 0;
    public static DateTimeOffset? Date(this JsonElement e) => DateTimeOffset.TryParse(e.Text(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
}
