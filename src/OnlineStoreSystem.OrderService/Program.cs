// File: src/OnlineStoreSystem.OrderService/Program.cs
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using OnlineStoreSystem.OrderService.Application;
using OnlineStoreSystem.OrderService.Clients;
using OnlineStoreSystem.OrderService.Data;
using OnlineStoreSystem.OrderService.Messaging;
using OnlineStoreSystem.OrderService.Outbox;
using OnlineStoreSystem.OrderService.Saga;
using OnlineStoreSystem.Shared.Correlation;
using OnlineStoreSystem.Shared.Hosting;
using OnlineStoreSystem.Shared.Json;
using OnlineStoreSystem.Shared.Logging;
using OnlineStoreSystem.Shared.Messaging;
using OnlineStoreSystem.Shared.Problems;

const string ServiceName = "OrderService";

var builder = WebApplication.CreateBuilder(args);

builder.AddOnlineStoreSerilog(ServiceName);

builder.Services.AddCorrelationId();
builder.Services.AddOnlineStoreProblemDetails(ServiceName, mappings => mappings
    .Map<DbUpdateConcurrencyException>(
        StatusCodes.Status409Conflict,
        ProblemTypes.ConcurrencyConflict,
        "The order was modified concurrently. Reload it and retry."));
builder.Services.AddSingleton(TimeProvider.System);

// ---- Persistence: scaffolded OrderDbContext (Orders, OrderItems, SagaStates, SagaStepLogs, OutboxMessages) ----
var connectionString = builder.Configuration.GetConnectionString("OrderDb")
    ?? throw new InvalidOperationException("Connection string 'ConnectionStrings:OrderDb' is not configured.");

builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseSqlServer(connectionString, sql =>
    {
        sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
        sql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
    }));

// Card data waiting for the asynchronous payment step is encrypted at rest (SagaStates.ProtectedPaymentDetails).
builder.Services.AddDataProtection().SetApplicationName("OnlineStoreSystem.OrderService");

// ---- Options ----
builder.Services.AddOptions<DownstreamServicesOptions>()
    .Bind(builder.Configuration.GetSection(DownstreamServicesOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<SagaOptions>()
    .Bind(builder.Configuration.GetSection(SagaOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<OutboxOptions>()
    .Bind(builder.Configuration.GetSection(OutboxOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var catalogResilience = LoadResilienceSettings(builder.Configuration, "Resilience:Catalog");
var paymentResilience = LoadResilienceSettings(builder.Configuration, "Resilience:Payment");

// ---- Module 3: typed HttpClients via IHttpClientFactory + standard resilience pipeline ----
// Handler order (outer -> inner): CorrelationIdDelegatingHandler -> resilience (total timeout, retry with
// exponential backoff + jitter, circuit breaker, attempt timeout) -> SocketsHttpHandler.
builder.Services.AddHttpClient<CatalogClient>((services, client) =>
        client.BaseAddress = ToBaseUri(services.GetRequiredService<IOptions<DownstreamServicesOptions>>().Value.CatalogBaseUrl))
    .AddCorrelationIdPropagation()
    .AddStandardResilienceHandler()
    .Configure((options, services) =>
        ResiliencePolicies.Apply(options, catalogResilience, services, CatalogClient.DependencyName));

builder.Services.AddHttpClient<PaymentClient>((services, client) =>
        client.BaseAddress = ToBaseUri(services.GetRequiredService<IOptions<DownstreamServicesOptions>>().Value.PaymentBaseUrl))
    .AddCorrelationIdPropagation()
    .AddStandardResilienceHandler()
    .Configure((options, services) =>
        ResiliencePolicies.Apply(options, paymentResilience, services, PaymentClient.DependencyName));

// ---- Application + saga orchestration ----
builder.Services.AddScoped<ProductValidator>();
builder.Services.AddScoped<OrderCommandService>();
builder.Services.AddScoped<OrderSagaOrchestrator>();
builder.Services.AddSingleton<PaymentDetailsProtector>();

// ---- Module 4: RabbitMQ (publisher confirms) + transactional outbox relay + reply consumer ----
builder.Services.AddRabbitMqMessaging(builder.Configuration);
builder.Services.AddSingleton<OutboxSignal>();
builder.Services.AddHostedService<OutboxDispatcher>();
builder.Services.AddHostedService<InventoryReplyConsumer>();
builder.Services.AddHostedService<SagaRecoveryService>();

// ---- API ----
builder.Services.AddControllers()
    .AddJsonOptions(options => JsonDefaults.Apply(options.JsonSerializerOptions));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.SwaggerDoc("v1", new OpenApiInfo
{
    Title = "OnlineStoreSystem Orders API (generated)",
    Version = "v1",
    Description = "Generated from code. The authoritative contract is contracts/orders-v1.yaml."
}));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<OrderDbContext>("order-db")
    .AddCheck<RabbitMqHealthCheck>("rabbitmq");

var app = builder.Build();

app.UseCorrelationId();
app.UseOnlineStoreRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.DocumentTitle = "Orders API - OnlineStoreSystem";
    options.SwaggerEndpoint("/contracts/orders-v1.yaml", "Orders API v1 (contract-first YAML)");
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Orders API v1 (generated from code)");
});

app.MapControllers();
app.MapApiContracts();
app.MapOnlineStoreHealthChecks(ServiceName);
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.Run();

static HttpResilienceSettings LoadResilienceSettings(IConfiguration configuration, string sectionPath)
{
    var settings = configuration.GetSection(sectionPath).Get<HttpResilienceSettings>() ?? new HttpResilienceSettings();
    Validator.ValidateObject(settings, new ValidationContext(settings), validateAllProperties: true);
    if (settings.TotalTimeoutSeconds <= settings.AttemptTimeoutSeconds)
    {
        throw new ValidationException(
            $"{sectionPath}: TotalTimeoutSeconds ({settings.TotalTimeoutSeconds}) must be greater than AttemptTimeoutSeconds ({settings.AttemptTimeoutSeconds}).");
    }

    return settings;
}

static Uri ToBaseUri(string url) => new(url.EndsWith('/') ? url : url + "/");
