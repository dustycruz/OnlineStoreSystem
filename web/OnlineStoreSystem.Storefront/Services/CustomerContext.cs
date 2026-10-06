// File: web/OnlineStoreSystem.Storefront/Services/CustomerContext.cs
namespace OnlineStoreSystem.Storefront.Services;

/// <summary>
/// Anonymous shopper identity (demo - no authentication): a random customer id kept in a long-lived cookie,
/// plus the last name / e-mail used at checkout for pre-filling the form.
/// </summary>
public sealed class CustomerContext(IHttpContextAccessor httpContextAccessor)
{
    private const string CustomerCookie = "OnlineStore.CustomerId";
    private const string NameCookie = "OnlineStore.CustomerName";
    private const string EmailCookie = "OnlineStore.CustomerEmail";

    private HttpContext Http =>
        httpContextAccessor.HttpContext ?? throw new InvalidOperationException("No active HTTP context.");

    public string CustomerId
    {
        get
        {
            if (Http.Request.Cookies.TryGetValue(CustomerCookie, out var existing) && !string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            if (Http.Items[CustomerCookie] is string generated)
            {
                return generated;
            }

            var customerId = $"cust-{Guid.NewGuid():N}"[..17];
            Http.Items[CustomerCookie] = customerId;
            Http.Response.Cookies.Append(CustomerCookie, customerId, LongLivedCookie());
            return customerId;
        }
    }

    public string? LastName => Http.Request.Cookies.TryGetValue(NameCookie, out var value) ? value : null;

    public string? LastEmail => Http.Request.Cookies.TryGetValue(EmailCookie, out var value) ? value : null;

    public void Remember(string name, string email)
    {
        Http.Response.Cookies.Append(NameCookie, name, LongLivedCookie());
        Http.Response.Cookies.Append(EmailCookie, email, LongLivedCookie());
    }

    private static CookieOptions LongLivedCookie() => new()
    {
        HttpOnly = true,
        IsEssential = true,
        SameSite = SameSiteMode.Lax,
        Expires = DateTimeOffset.UtcNow.AddYears(1)
    };
}
