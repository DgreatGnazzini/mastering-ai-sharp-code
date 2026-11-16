using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PriorAuthBot.Core;
using PriorAuthBot.Core.Hosting;
using PriorAuthBot.Core.Observability;

namespace PriorAuthBot.Api.Observability;

/// <summary>
/// Chapter 13: where PriorAuthBot's traces, metrics, and logs go. Core only writes to
/// <see cref="System.Diagnostics.ActivitySource"/>, <see cref="System.Diagnostics.Metrics.Meter"/>,
/// and <see cref="ILogger"/>; this is the one place that names an exporter. Under Aspire, the
/// AppHost sets OTEL_EXPORTER_OTLP_ENDPOINT to its dashboard. In Azure (chapter 14), the same OTLP
/// exporter can point at an OpenTelemetry collector, and chapter 14 adds Azure Monitor: when an
/// Application Insights connection string is set, traces, metrics, and logs go there too, signed in
/// with the app's managed identity (the Application Insights resource has local authentication off,
/// so the connection string alone cannot send anything). With neither, nothing is exported.
/// </summary>
internal static class TelemetrySetup
{
    public const string ServiceName = "priorauthbot-api";

    public static void AddPriorAuthBotTelemetry(this WebApplicationBuilder builder)
    {
        var otlp = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        var azureMonitor = builder.Configuration["Telemetry:AzureMonitor:ConnectionString"] ?? builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        var credential = string.IsNullOrWhiteSpace(azureMonitor) ? null : AzureIdentity.CreateCredential(AzureIdentity.Options(builder.Configuration));
        void AzureMonitor(AzureMonitorExporterOptions options)
        {
            options.ConnectionString = azureMonitor;
            options.Credential = credential;
        }

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            if (credential is not null)
            {
                logging.AddAzureMonitorLogExporter(AzureMonitor);
            }
        });

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName, serviceVersion: PriorAuthBotInfo.CurrentChapter))
            .WithTracing(tracing => tracing
                .AddSource(
                    PriorAuthBotTelemetry.SourceName,   // ask, retrieve
                    PriorAuthBotTelemetry.AiSourceName, // gen_ai chat, embeddings, execute_tool
                    "Microsoft.AspNetCore",             // one server span per request (.NET 10 emits it natively)
                    "System.Net.Http"))                 // calls to Ollama, Azure OpenAI, and the HR API
            .WithMetrics(metrics => metrics
                .AddMeter(
                    PriorAuthBotTelemetry.SourceName,   // tokens, cost, quota refusals, cache, audit, feedback
                    PriorAuthBotTelemetry.AiSourceName, // gen_ai.client.token.usage and operation.duration
                    "Microsoft.AspNetCore.Hosting",
                    "Microsoft.AspNetCore.Server.Kestrel",
                    "System.Runtime",                   // GC, thread pool, CPU (.NET 9 and later)
                    "System.Net.Http"));

        if (otlp)
        {
            telemetry.UseOtlpExporter();
        }

        if (credential is not null)
        {
            telemetry
                .WithTracing(tracing => tracing.AddAzureMonitorTraceExporter(AzureMonitor))
                .WithMetrics(metrics => metrics.AddAzureMonitorMetricExporter(AzureMonitor));
        }
    }
}
