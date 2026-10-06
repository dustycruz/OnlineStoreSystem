// File: src/OnlineStoreSystem.InventoryService/Program.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using OnlineStoreSystem.InventoryService.Data;
using OnlineStoreSystem.InventoryService.Messaging;
using OnlineStoreSystem.InventoryService.Services;
using OnlineStoreSystem.Shared.Correlation;
using OnlineStoreSystem.Shared.Hosting;
using OnlineStoreSystem.Shared.Json;
using OnlineStoreSystem.Shared.Logging;
using OnlineStoreSystem.Shared.Messaging;
using OnlineStoreSystem.Shared.Problems;

const string ServiceName = "InventoryService";

var builder = WebApplication.CreateBuilder(args);

builder.AddOnlineStoreSerilog(ServiceName);

builder.Services.AddCorrelationId();
builder.Services.AddOnlineStoreProblemDetails(ServiceName, mappings => mappings
    .Map<DbUpdateConcurrencyException>(
        StatusCodes.Status409Conflict,
        ProblemTypes.ConcurrencyConflict,
        "The stock record was modified concurrently. Retry the request."));
builder.Services.AddSingleton(TimeProvider.System);

// ---- Persistence: scaffolded InventoryDbContext (InventoryItems, StockReservations, ProcessedMessages) ----
var connectionString = builder.Configuration.GetConnectionString("InventoryDb")
    ?? throw new InvalidOperationException("Connection string 'ConnectionStrings:InventoryDb' is not configured.");

builder.Services.AddDbContext<InventoryDbContext>(options =>
    options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(
        maxRetryCount: 5,
        maxRetryDelay: TimeSpan.FromSeconds(10),
        errorNumbersToAdd: null)));

builder.Services.AddScoped<StockReservationService>();
builder.Services.AddScoped<ProcessedMessagesIdempotencyGuard>();

// ---- Messaging: RabbitMQ connection, confirm-based publisher, idempotent consumers with DLQ ----
builder.Services.AddRabbitMqMessaging(builder.Configuration);
builder.Services.AddHostedService<OrderPlacedConsumer>();
builder.Services.AddHostedService<OrderOutcomeConsumer>();

// ---- API ----
builder.Services.AddControllers()
    .AddJsonOptions(options => JsonDefaults.Apply(options.JsonSerializerOptions));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.SwaggerDoc("v1", new OpenApiInfo
{
    Title = "OnlineStoreSystem Inventory API (generated)",
    Version = "v1",
    Description = "Generated from code. The authoritative contract is contracts/inventory-v1.yaml."
}));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<InventoryDbContext>("inventory-db")
    .AddCheck<RabbitMqHealthCheck>("rabbitmq");

var app = builder.Build();

app.UseCorrelationId();
app.UseOnlineStoreRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.DocumentTitle = "Inventory API - OnlineStoreSystem";
    options.SwaggerEndpoint("/contracts/inventory-v1.yaml", "Inventory API v1 (contract-first YAML)");
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Inventory API v1 (generated from code)");
});

app.MapControllers();
app.MapApiContracts();
app.MapOnlineStoreHealthChecks(ServiceName);
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.Run();
