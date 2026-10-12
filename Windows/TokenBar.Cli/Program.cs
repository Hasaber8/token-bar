using System.Text.Json;
using TokenBar.Core;

try
{
    var period = UsagePeriod.Day; var relative = false; var json = false; string? home = null;
    for (var i = 0; i < args.Length; i++)
        switch (args[i])
        {
            case "--print": break;
            case "--json": json = true; break;
            case "--relative": relative = true; break;
            case "--home" when i + 1 < args.Length: home = Path.GetFullPath(args[++i]); break;
            case "--period" when i + 1 < args.Length:
                if (!Enum.TryParse(args[++i], true, out period) || !Enum.IsDefined(period)) throw new ArgumentException("Period must be day, week, month, or year.");
                break;
            case "--help": Console.WriteLine("token-bar-cli [--print] [--period day|week|month|year] [--relative] [--json] [--home PATH]"); return 0;
            default: throw new ArgumentException("Unknown or incomplete option: " + args[i]);
        }
    var paths = home is null ? SourcePaths.Discover() : SourcePaths.Discover(home, _ => null);
    var sources = new Scanner().Scan(paths, new ReportingRange(period, relative));
    if (json) Console.WriteLine(JsonSerializer.Serialize(sources, new JsonSerializerOptions { WriteIndented = true }));
    else
    {
        foreach (var source in sources)
        {
            if (source.Error is { } error) Console.Error.WriteLine(source.Name + ": " + error);
            foreach (var (model, usage) in source.PerModel.OrderByDescending(m => m.Value.Cost))
                Console.WriteLine($"{source.Name} / {model}: in={usage.Input:F0} out={usage.Output:F0} cache_read={usage.CacheRead:F0} cache_write={usage.CacheWrite:F0} cost={(source.EstimatedPricing.Contains(model) ? "~" : "")}{Format.Money(usage.Cost)}");
        }
        var total = sources.Aggregate(new Usage(), (a, source) => a + source.Total);
        Console.WriteLine($"TOTAL: spend={Format.Money(total.Cost)} in={total.Input:F0} out={total.Output:F0} hit={total.HitRate:P1}");
    }
    return sources.Any(s => s.Error is not null) ? 1 : 0;
}
catch (Exception ex) when (ex is ArgumentException or IOException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
