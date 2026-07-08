using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreditMeter;

/// <summary>Time period portion of the billing usage response.</summary>
public sealed record BillingTimePeriod(
    [property: JsonPropertyName("year")] int Year);

/// <summary>A single usage line item within the billing usage response.</summary>
public sealed record BillingUsageItem(
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("sku")] string Sku,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("unitType")] string UnitType,
    [property: JsonPropertyName("pricePerUnit")] decimal PricePerUnit,
    [property: JsonPropertyName("grossQuantity")] decimal GrossQuantity,
    [property: JsonPropertyName("grossAmount")] decimal GrossAmount,
    [property: JsonPropertyName("discountQuantity")] decimal DiscountQuantity,
    [property: JsonPropertyName("discountAmount")] decimal DiscountAmount,
    [property: JsonPropertyName("netQuantity")] decimal NetQuantity,
    [property: JsonPropertyName("netAmount")] decimal NetAmount);

/// <summary>Root shape of the GitHub AI credit usage billing response.</summary>
public sealed record BillingUsageResponse(
    [property: JsonPropertyName("timePeriod")] BillingTimePeriod TimePeriod,
    [property: JsonPropertyName("user")] string User,
    [property: JsonPropertyName("usageItems")] List<BillingUsageItem> UsageItems);

/// <summary>
/// Source-generated JSON (de)serialization context. Required for Native AOT:
/// it lets System.Text.Json work without runtime reflection.
/// </summary>
[JsonSerializable(typeof(BillingUsageResponse))]
internal partial class GitHubApiJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Result of a spend fetch attempt. <see cref="Error"/> is a short, sanitized
/// failure reason (e.g. "HTTP 403", "Network error") suitable for display —
/// it never contains the PAT/token. Null on success. <see cref="Credits"/> is
/// the AI credits used this month, derived alongside <see cref="Spend"/> from
/// the same usage items.
///
/// <see cref="RequestUrl"/>, <see cref="HttpStatus"/> and
/// <see cref="ResponseBodySnippet"/> are temporary sanitized diagnostics
/// (never the PAT) surfaced by --test-api to help debug the billing API
/// response shape; they're populated on every call, success or failure.
/// </summary>
internal readonly record struct GitHubSpendResult(
    decimal? Spend,
    decimal? Credits,
    string? Error,
    string? RequestUrl = null,
    int? HttpStatus = null,
    string? ResponseBodySnippet = null);

/// <summary>
/// Calls GitHub's personal billing API to fetch the current month's Copilot
/// AI credit usage. The endpoint requires a fine-grained personal access
/// token with the "Plan" read-only user permission — not the org-only
/// "manage_billing:copilot" scope used by the older organization billing API.
/// </summary>
public sealed class GitHubApiClient
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>
    /// Fetches the current month's AI credit usage for <paramref name="username"/>
    /// and returns the sum of <c>netAmount</c> (dollar spend) and the AI credits
    /// used across all usage items, along with a short sanitized failure reason
    /// if the call didn't succeed. Never throws — this is used both by the
    /// background poller and by --test-api.
    /// </summary>
    internal async Task<GitHubSpendResult> GetCurrentMonthNetSpendDetailedAsync(string username, string pat)
    {
        string requestUrl = $"https://api.github.com/users/{username}/settings/billing/ai_credit/usage";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CreditMeter", null));

            using HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);
            int httpStatus = (int)response.StatusCode;

            // Read as text (not stream-deserialize directly) so we always have a
            // body available for diagnostics, whether or not parsing succeeds.
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            string bodySnippet = body.Length > 1000 ? body[..1000] : body;

            if (!response.IsSuccessStatusCode)
            {
                return new GitHubSpendResult(null, null, $"HTTP {httpStatus}", requestUrl, httpStatus, bodySnippet);
            }

            BillingUsageResponse? usage;
            try
            {
                usage = JsonSerializer.Deserialize(body, GitHubApiJsonContext.Default.BillingUsageResponse);
            }
            catch (JsonException)
            {
                // Response parsed as HTTP 200 but the JSON shape didn't match what we
                // expect — surface that clearly instead of silently reporting 0.
                return new GitHubSpendResult(null, null, "JSON parse error", requestUrl, httpStatus, bodySnippet);
            }

            if (usage?.UsageItems is null)
            {
                return new GitHubSpendResult(null, null, "API returned no spend", requestUrl, httpStatus, bodySnippet);
            }

            decimal spend = 0m;
            decimal credits = 0m;
            bool foundCreditQuantity = false;

            try
            {
                foreach (BillingUsageItem item in usage.UsageItems)
                {
                    // netAmount is the actual dollar amount billed — it's $0 whenever
                    // usage is fully covered by the plan's included monthly allowance,
                    // which is the common case and not itself a bug.
                    spend += item.NetAmount;

                    // The billing API isn't consistent about the exact unitType string
                    // for AI credits (seen: "ai-credits", "credits") — match loosely.
                    if (!string.IsNullOrEmpty(item.UnitType) &&
                        item.UnitType.Contains("credit", StringComparison.OrdinalIgnoreCase))
                    {
                        // grossQuantity is the actual number of AI credits consumed
                        // this period — the same figure GitHub's billing page shows.
                        // netQuantity/netAmount only reflect the portion billed *beyond*
                        // the plan's included allowance, so they're 0 for most users
                        // even though real, non-zero credit usage occurred.
                        credits += item.GrossQuantity;
                        foundCreditQuantity = true;
                    }
                }
            }
            catch (Exception)
            {
                // usageItems existed but something about summing its fields blew up
                // (unexpected nulls, etc.) — never silently fall back to 0.
                return new GitHubSpendResult(null, null, "JSON parse error", requestUrl, httpStatus, bodySnippet);
            }

            if (!foundCreditQuantity && spend != 0m)
            {
                // No usage item reported a credit-unit quantity directly — derive
                // credits from spend using GitHub's published $0.01-per-credit rate.
                credits = spend / 0.01m;
            }

            return new GitHubSpendResult(spend, credits, null, requestUrl, httpStatus, bodySnippet);
        }
        catch (Exception)
        {
            // Network failure, timeout, etc. — swallow and report a short,
            // sanitized reason instead of crashing the timer.
            return new GitHubSpendResult(null, null, "Network error", requestUrl, null, null);
        }
    }

    /// <summary>
    /// Fetches the current month's AI credit usage and returns just the spend,
    /// discarding credits and the failure reason. Returns null on any error. Kept
    /// for callers that don't need the detailed result.
    /// </summary>
    public async Task<decimal?> GetCurrentMonthNetSpendAsync(string username, string pat)
    {
        GitHubSpendResult result = await GetCurrentMonthNetSpendDetailedAsync(username, pat).ConfigureAwait(false);
        return result.Spend;
    }
}
