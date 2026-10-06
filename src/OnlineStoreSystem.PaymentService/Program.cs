// File: src/OnlineStoreSystem.PaymentService/Program.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using OnlineStoreSystem.PaymentService.Data;
using OnlineStoreSystem.PaymentService.Services;
using OnlineStoreSystem.Shared.Correlation;
using OnlineStoreSystem.Shared.Diagnostics;
using OnlineStoreSystem.Shared.Hosting;
using OnlineStoreSystem.Shared.Json;
using OnlineStoreSystem.Shared.Logging;
using OnlineStoreSystem.Shared.Problems;

const string ServiceName = "PaymentService";

var builder = WebApplication.CreateBuilder(args);

builder.AddOnlineStoreSerilog(ServiceName);

builder.Services.AddCorrelationId();
builder.Services.AddOnlineStoreProblemDetails(ServiceName, mappings => mappings
    .Map<DbUpdateConcurrencyException>(
        StatusCodes.Status409Conflict,
        ProblemTypes.ConcurrencyConflict,
        "The payment was modified concurrently (e.g. two refunds at once). Reload and retry."));
builder.Services.AddFaultInjection(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);

// ---- Persistence: scaffolded PaymentDbContext (PaymentTransactions, PaymentRefunds) ----
var connectionString = builder.Configuration.GetConnectionString("PaymentDb")
    ?? throw new InvalidOperationException("Connection string 'ConnectionStrings:PaymentDb' is not configured.");

builder.Services.AddDbContext<PaymentDbContext>(options =>
    options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(
        maxRetryCount: 5,
        maxRetryDelay: TimeSpan.FromSeconds(10),
        errorNumbersToAdd: null)));

// ---- Payment domain ----
builder.Services.Configure<PaymentGatewayOptions>(builder.Configuration.GetSection(PaymentGatewayOptions.SectionName));
builder.Services.AddSingleton<IPaymentGateway, SimulatedPaymentGateway>();
builder.Services.AddScoped<PaymentProcessor>();

// ---- API ----
builder.Services.AddControllers()
    .AddJsonOptions(options => JsonDefaults.Apply(options.JsonSerializerOptions));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.SwaggerDoc("v1", new OpenApiInfo
{
    Title = "OnlineStoreSystem Payment API (generated)",
    Version = "v1",
    Description = "Generated from code. The authoritative contract is contracts/payment-v1.yaml."
}));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<PaymentDbContext>("payment-db");

var app = builder.Build();

app.UseCorrelationId();
app.UseOnlineStoreRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseFaultInjection();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.DocumentTitle = "Payment API - OnlineStoreSystem";
    options.SwaggerEndpoint("/contracts/payment-v1.yaml", "Payment API v1 (contract-first YAML)");
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Payment API v1 (generated from code)");
});

app.MapControllers();
app.MapApiContracts();
app.MapOnlineStoreHealthChecks(ServiceName);
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.Run();
