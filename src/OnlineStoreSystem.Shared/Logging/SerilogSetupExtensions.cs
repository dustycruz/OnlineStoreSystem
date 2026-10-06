// File: src/OnlineStoreSystem.Shared/Logging/SerilogSetupExtensions.cs
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OnlineStoreSystem.Shared.Correlation;
using Serilog;
using Serilog.Events;

namespace OnlineStoreSystem.Shared.Logging;

/// <summary>
/// Serilog configuration shared by all five applications.
/// <list type="bullet">
///   <item>Console + daily rolling file per service in <c>&lt;repo&gt;/logs/</c> so a single
///         <c>grep &lt;correlation-id&gt; logs/*.log</c> shows a request across every service.</item>
///   <item><c>Enrich.FromLogContext()</c> turns <c>ILogger.BeginScope</c> dictionaries (CorrelationId,
///         MessageId...) into structured properties.</item>
///   <item>Minimum levels are read from the <c>Serilog</c> configuration section.</item>
/// </list>
/// </summary>
public static class SerilogSetupExtensions
{
    public const string OutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] [{Application}] [cid:{CorrelationId}] {SourceContext}: {Message:l}{NewLine}{Exception}";

    extension(WebApplicationBuilder builder)
    {
        public WebApplicationBuilder AddOnlineStoreSerilog(string serviceName)
        {
            var logDirectory = ResolveLogDirectory(builder.Configuration, builder.Environment.ContentRootPath);
            var fileName = ToKebabCase(serviceName);

            builder.Services.AddSerilog((services, configuration) => configuration
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", serviceName)
                .WriteTo.Console(outputTemplate: OutputTemplate)
                .WriteTo.File(
                    path: Path.Combine(logDirectory, $"{fileName}-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    shared: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1),
                    outputTemplate: OutputTemplate));

            return builder;
        }
    }

    extension(WebApplication app)
    {
        /// <summary>One structured line per HTTP request, enriched with the correlation id.</summary>
        public WebApplication UseOnlineStoreRequestLogging()
        {
            app.UseSerilogRequestLogging(options =>
            {
                options.MessageTemplate =
                    "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0} ms";
                options.GetLevel = (httpContext, _, exception) =>
                    exception is not null || httpContext.Response.StatusCode >= 500
                        ? LogEventLevel.Error
                        : httpContext.Request.Path.StartsWithSegments("/health")
                            ? LogEventLevel.Verbose
                            : LogEventLevel.Information;
                options.EnrichDiagnosticContext = (diagnostics, httpContext) =>
                {
                    diagnostics.Set(CorrelationIdConstants.LogPropertyName, httpContext.CorrelationId ?? string.Empty);
                    diagnostics.Set("ClientIp", httpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty);
                };
            });

            return app;
        }
    }

    /// <summary>
    /// <c>Logging:Directory</c> wins when configured; otherwise the folder that contains
    /// <c>OnlineStoreSystem.sln</c> (walking up from the content root) + <c>/logs</c>.
    /// </summary>
    public static string ResolveLogDirectory(IConfiguration configuration, string contentRootPath)
    {
        var configured = configuration["Logging:Directory"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(contentRootPath, configured));
        }

        var directory = new DirectoryInfo(contentRootPath);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OnlineStoreSystem.sln")))
            {
                return Path.Combine(directory.FullName, "logs");
            }

            directory = directory.Parent;
        }

        return Path.Combine(contentRootPath, "logs");
    }

    private static string ToKebabCase(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsUpper(c) && i > 0)
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
