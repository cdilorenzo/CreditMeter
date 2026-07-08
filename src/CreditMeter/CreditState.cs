namespace CreditMeter;

/// <summary>
/// Latest in-memory snapshot produced by the background polling loop.
/// The tray tooltip and the meter popup both just read this — neither
/// calls the GitHub API directly.
/// </summary>
public sealed class CreditState
{
    public decimal? CurrentMonthSpend { get; set; }

    /// <summary>AI credits used this month, derived from the billing API's usage items.</summary>
    public decimal? CurrentMonthCredits { get; set; }

    /// <summary>User-configured local monthly credit limit (not fetched from any API). Null if not set.</summary>
    public decimal? MonthlyCreditLimit { get; set; }

    public bool IsConfigured { get; set; }
    public bool ApiUnavailable { get; set; }
    public bool IsLoading { get; set; }
    public DateTimeOffset? LastUpdated { get; set; }

    /// <summary>
    /// Short, sanitized failure reason from the last failed poll (e.g. "HTTP 403",
    /// "Network error", "API returned no spend"). Never contains the PAT/token.
    /// Null when the last poll succeeded or hasn't run yet.
    /// </summary>
    public string? LastError { get; set; }
}
