using System.Text.Json;

namespace TokenBar.Core;

public sealed class PricingCatalog
{
    private readonly JsonElement providers;
    public PricingCatalog(string json) { using var doc = JsonDocument.Parse(json); providers = doc.RootElement.Get("providers").Clone(); }
    public static PricingCatalog Bundled { get; } = Load();
    private static PricingCatalog Load()
    {
        using var stream = typeof(PricingCatalog).Assembly.GetManifestResourceStream("model-pricing.json")!;
        using var reader = new StreamReader(stream);
        return new(reader.ReadToEnd());
    }
    public static string ProviderId(string value) => value.Trim().ToLowerInvariant() switch
    {
        "amazon bedrock" or "amazon_bedrock" or "aws-bedrock" or "bedrock" or "bedrock-mantle" => "amazon-bedrock",
        "open ai" or "open_ai" => "openai",
        var id => id
    };
    public (double Cost, bool Estimated) Price(string provider, string model, Usage usage, double reasoning = 0)
    {
        var providersToTry = ProviderCandidates(ProviderId(provider)).ToArray();
        var modelsToTry = ModelCandidates(model).Distinct(StringComparer.Ordinal).ToArray();
        for (var m = 0; m < modelsToTry.Length; m++)
        for (var p = 0; p < providersToTry.Length; p++)
        {
            var rates = providers.Get(providersToTry[p]).Get("models").Get(modelsToTry[m]);
            if (rates.ValueKind != JsonValueKind.Object) continue;
            var tier = default(JsonElement);
            var tiers = rates.Get("tiers");
            if (tiers.ValueKind == JsonValueKind.Array)
                foreach (var candidate in tiers.EnumerateArray())
                    if (candidate.Get("tier").Get("type").Text() == "context" && usage.Context > candidate.Get("tier").Get("size").Num()
                        && (tier.ValueKind == JsonValueKind.Undefined || candidate.Get("tier").Get("size").Num() > tier.Get("tier").Get("size").Num())) tier = candidate;
            JsonElement Rate(string key) => tier.Get(key).ValueKind == JsonValueKind.Number ? tier.Get(key) : rates.Get(key);
            if (usage.CacheRead > 0 && Rate("cache_read").ValueKind != JsonValueKind.Number ||
                usage.CacheWrite > 0 && Rate("cache_write").ValueKind != JsonValueKind.Number) continue;
            var reasonRate = Rate("reasoning").ValueKind == JsonValueKind.Number ? Rate("reasoning").Num() : Rate("output").Num();
            return ((usage.Input * Rate("input").Num() + Math.Max(0, usage.Output - reasoning) * Rate("output").Num()
                + reasoning * reasonRate + usage.CacheRead * Rate("cache_read").Num() + usage.CacheWrite * Rate("cache_write").Num()) / 1_000_000, m > 0 || p > 0);
        }
        return (0, true);
    }
    private static IEnumerable<string> ProviderCandidates(string id)
    {
        while (id.Length > 0)
        {
            yield return id;
            var index = id.LastIndexOfAny(['-', '_', '/']);
            if (index < 0) yield break;
            id = id[..index];
        }
    }
    private static IEnumerable<string> ModelCandidates(string id)
    {
        while (id.Length > 0)
        {
            yield return id;
            for (var i = 0; i < id.Length - 1; i++) if (id[i] is '/' or '.') yield return id[(i + 1)..];
            var index = id.LastIndexOf('-');
            if (index < 0) yield break;
            id = id[..index];
        }
    }
}
