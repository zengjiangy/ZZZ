namespace ZZZ.Models;

public enum AdBlockUpdateInterval
{
    Manual,
    Daily,
    Weekly
}

public enum AdBlockResourceType
{
    Other,
    Document,
    Subdocument,
    Script,
    Stylesheet,
    Image,
    Font,
    Media,
    Object,
    XmlHttpRequest,
    WebSocket,
    Ping
}

public sealed class AdBlockRequestContext
{
    public string Url { get; set; } = string.Empty;
    public string DocumentUrl { get; set; } = string.Empty;
    public AdBlockResourceType ResourceType { get; set; } = AdBlockResourceType.Other;
    public string Method { get; set; } = "GET";
}

public sealed class AdBlockConfiguration
{
    public int SchemaVersion { get; set; } = 1;
    public AdBlockUpdateInterval UpdateInterval { get; set; } = AdBlockUpdateInterval.Daily;
    public DateTime? LastAutomaticUpdateCheckUtc { get; set; }
    public List<AdBlockSubscription> Subscriptions { get; set; } = [];
}

public sealed class AdBlockSubscription
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool IsBuiltIn { get; set; }
    public string CacheFileName { get; set; } = string.Empty;
    public int RuleCount { get; set; }
    public DateTime? LastUpdatedUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public string ETag { get; set; } = string.Empty;
    public DateTimeOffset? LastModified { get; set; }
    public string LastError { get; set; } = string.Empty;
}

public enum AdBlockUpdateOutcome
{
    Updated,
    NotModified,
    Skipped,
    Failed
}

public sealed class AdBlockUpdateResult
{
    public string SubscriptionId { get; set; } = string.Empty;
    public string SubscriptionName { get; set; } = string.Empty;
    public AdBlockUpdateOutcome Outcome { get; set; }
    public int RuleCount { get; set; }
    public string Error { get; set; } = string.Empty;
}

public sealed class AdBlockRuleStatistics
{
    public int NetworkBlockingRules { get; set; }
    public int NetworkExceptionRules { get; set; }
    public int CosmeticRules { get; set; }
    public int CosmeticExceptionRules { get; set; }
    public int IgnoredRules { get; set; }
    public int TotalActiveRules => NetworkBlockingRules + NetworkExceptionRules + CosmeticRules + CosmeticExceptionRules;
}

public sealed class AdBlockElementRule
{
    public string PageUrl { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Selector { get; set; } = string.Empty;
    public string FilterText { get; set; } = string.Empty;
}
