// File: web/OnlineStoreSystem.Storefront/Program.cs
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using OnlineStoreSystem.Shared.Correlation;
using OnlineStoreSystem.Shared.Hosting;
using OnlineStoreSystem.Shared.Logging;
using OnlineStoreSystem.Storefront.Clients;
using OnlineStoreSystem.Storefront.Services;
using Polly;

const string AppName = "Storefront";

var builder = WebApplication.CreateBuilder(args);

builder.AddOnlineStoreSerilog(AppName);

// The Storefront is the EDGE of the system: CorrelationIdMiddleware creates the X-Correlation-Id for every
// browser request and CorrelationIdDelegatingHandler forwards it to every API call.
builder.Services.AddCorrelationId();

builder.Services.AddControllersWithViews();
builder.Services.AddHttpContextAccessor();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = "OnlineStore.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.IdleTimeout = TimeSpan.FromHours(2);
});

builder.Services.AddOptions<ApiEndpointsOptions>()
    .Bind(builder.Configuration.GetSection(ApiEndpointsOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// ---- Named HTTP clients (IHttpClientFactory) with correlation propagation + resilience ----
// StorefrontApiClient picks the right one per call: CatalogApi (5001), OrderApi (5002), InventoryApi (5003).
builder.Services.AddHttpClient(StorefrontApiClient.CatalogClientName, (services, client) =>
        client.BaseAddress = ApiEndpointsOptions.ToBaseUri(services.GetRequiredService<IOptions<ApiEndpointsOptions>>().Value.CatalogBaseUrl))
    .AddCorrelationIdPropagation()
    .AddStandardResilienceHandler(ConfigureUiResilience);

builder.Services.AddHttpClient(StorefrontApiClient.InventoryClientName, (services, client) =>
        client.BaseAddress = ApiEndpointsOptions.ToBaseUri(services.GetRequiredService<IOptions<ApiEndpointsOptions>>().Value.InventoryBaseUrl))
    .AddCorrelationIdPropagation()
    .AddStandardResilienceHandler(ConfigureUiResilience);

builder.Services.AddHttpClient(StorefrontApiClient.OrderClientName, (services, client) =>
        client.BaseAddress = ApiEndpointsOptions.ToBaseUri(services.GetRequiredService<IOptions<ApiEndpointsOptions>>().Value.OrderBaseUrl))
    .AddCorrelationIdPropagation()
    .AddStandardResilienceHandler(options =>
    {
        ConfigureUiResilience(options);
        // Checkout already waits for OrderService's own retries towards CatalogService.
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(45);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(90);
        // POST /checkout is NOT idempotent: never retry unsafe methods from the UI.
        options.Retry.DisableForUnsafeHttpMethods();
    });

builder.Services.AddScoped<StorefrontApiClient>();
builder.Services.AddScoped<CartService>();
builder.Services.AddScoped<CustomerContext>();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseCorrelationId();
app.UseOnlineStoreRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Home/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseSession();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapOnlineStoreHealthChecks(AppName);

app.Run();

static void ConfigureUiResilience(HttpStandardResilienceOptions options)
{
    // Pages should fail fast: 2 quick retries with exponential backoff + jitter, 5 s per attempt, 15 s total.
    options.Retry.MaxRetryAttempts = 2;
    options.Retry.BackoffType = DelayBackoffType.Exponential;
    options.Retry.UseJitter = true;
    options.Retry.Delay = TimeSpan.FromMilliseconds(300);
    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(15);
    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
}
