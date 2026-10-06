// File: src/OnlineStoreSystem.CatalogService/Program.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using OnlineStoreSystem.CatalogService.Data;
using OnlineStoreSystem.Shared.Correlation;
using OnlineStoreSystem.Shared.Diagnostics;
using OnlineStoreSystem.Shared.Hosting;
using OnlineStoreSystem.Shared.Json;
using OnlineStoreSystem.Shared.Logging;
using OnlineStoreSystem.Shared.Problems;

const string ServiceName = "CatalogService";

var builder = WebApplication.CreateBuilder(args);

// ---- Logging: Serilog (console + logs/catalog-service-*.log) with CorrelationId on every line ----
builder.AddOnlineStoreSerilog(ServiceName);

// ---- Cross-cutting: correlation id, RFC 7807 problem details, optional fault injection ----
builder.Services.AddCorrelationId();
builder.Services.AddOnlineStoreProblemDetails(ServiceName, mappings => mappings
    .Map<DbUpdateConcurrencyException>(
        StatusCodes.Status409Conflict,
        ProblemTypes.ConcurrencyConflict,
        "The product was modified by another request. Reload it and try again."));
builder.Services.AddFaultInjection(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);

// ---- Persistence: scaffolded CatalogDbContext on SQL Server (database-first) ----
var connectionString = builder.Configuration.GetConnectionString("CatalogDb")
    ?? throw new InvalidOperationException("Connection string 'ConnectionStrings:CatalogDb' is not configured.");

builder.Services.AddDbContext<CatalogDbContext>(options =>
    options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(
        maxRetryCount: 5,
        maxRetryDelay: TimeSpan.FromSeconds(10),
        errorNumbersToAdd: null)));

// ---- API: controllers + Swagger (generated doc shown next to the hand-written contract) ----
builder.Services.AddControllers()
    .AddJsonOptions(options => JsonDefaults.Apply(options.JsonSerializerOptions));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.SwaggerDoc("v1", new OpenApiInfo
{
    Title = "OnlineStoreSystem Catalog API (generated)",
    Version = "v1",
    Description = "Generated from code. The authoritative contract is contracts/catalog-v1.yaml."
}));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<CatalogDbContext>("catalog-db");

var app = builder.Build();

// ---- HTTP pipeline (order matters) ----
app.UseCorrelationId();               // 1. X-Correlation-Id + logging scope for everything below
app.UseOnlineStoreRequestLogging();   // 2. one Serilog line per request
app.UseExceptionHandler();            // 3. unhandled exceptions -> RFC 7807
app.UseStatusCodePages();             // 4. empty 4xx/5xx bodies -> RFC 7807
app.UseFaultInjection();              // 5. demo-only transient failures (disabled by default)

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.DocumentTitle = "Catalog API - OnlineStoreSystem";
    options.SwaggerEndpoint("/contracts/catalog-v1.yaml", "Catalog API v1 (contract-first YAML)");
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Catalog API v1 (generated from code)");
});

app.MapControllers();
app.MapApiContracts();
app.MapOnlineStoreHealthChecks(ServiceName);
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.Run();
