using Ariva.Api.Common.Security;
using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.HealthChecks;
using Ariva.Api.Common.Hosting;
using Ariva.Api.Common.Filters;
using Ariva.Api.Ingest.Endpoints;
using Ariva.Api.Ingest.Mqtt;
using Ariva.Di;
using Ariva.Di.Extensions;

// Ariva.Api.Ingest: sensor adapters. Vendor webhooks (Endpoints/) and pollers (Adapters/)
// normalise sensor readings and produce them to Kafka.
// The regions follow AMAN's Program.cs order. Calls named in comments are AMAN extensions that backlog
// stories port into Ariva.Api.Common; until then this file calls framework APIs, RegisterArivaServices,
// the security baseline and MapArivaHealthChecks only, so it builds without NuGet packages.

#region Configuration

// AMAN: Util.GlobalDisableReloadOnChange(); var environment = Util.GetOrCreateEnvironment();
//       builder.Configuration.AddAppConfiguration(environment);
// Until that story lands, the layering is done here with framework APIs only:
// appsettings.base.json, appsettings.base.<env>.json, appsettings.service.json, appsettings.service.<env>.json,
// then environment variables and command line arguments, which keep the highest precedence.
// The environment is also the host environment name, so error handling sees the same value as the files.
var environment = ArivaEnvironment.Resolve(args, AppContext.BaseDirectory);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    EnvironmentName = environment
});

builder.Configuration
    .AddJsonFile("appsettings.base.json", optional: false, reloadOnChange: false)
    .AddJsonFile($"appsettings.base.{environment}.json", optional: true, reloadOnChange: false)
    .AddJsonFile("appsettings.service.json", optional: false, reloadOnChange: false)
    .AddJsonFile($"appsettings.service.{environment}.json", optional: true, reloadOnChange: false)
    // Local development only: written by scripts/dev-up.mjs from .env; git-ignored and never in images.
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

#endregion

#region Logging, Hosting and Telemetry

// Serilog with secret redaction, JSON console, Loki and OTLP by configuration; traces and metrics over OTLP (ARV-007).
builder.AddAppLogging();
builder.AddAppServiceDefaults();
// AMAN: builder.Host.AddAppHosting(); builder.WebHost.AddAppWebHost(...);

#endregion

#region Services

builder.Services.RegisterArivaServices(builder.Configuration);

// Security baseline (docs/security/cwe-controls.md): default deny (fallback policy, Ariva.Deny answers challenges),
// ES256 access tokens from Ariva.Api.Main with permissions from stored grants (ARV-010a), input limits, rate limiting,
// ProblemDetails errors, trusted forwarded headers and the CORS allow-list.
builder.Services.AddAppSecurityBaseline(builder.Configuration, builder.Environment.EnvironmentName);

// Device endpoints (ARV-022): controllers with the unit of work filter, and client certificates forwarded by the
// ingress for devices that pin one.
builder.Services.AddAppControllers();
// OpenAPI document for the dynamic scan (ARV-063): only with OpenApi:Enabled, administrators only, refused in k8s-prd.
builder.Services.AddArivaOpenApi(builder.Configuration, builder.Environment.EnvironmentName);
builder.Services.AddAppClientCertificates(builder.Configuration);

// Sensor pushes (ARV-023): dialect mappers, per-device clock estimates, batches to Kafka keyed by zone.
builder.Services.AddArivaSensingIngest(builder.Configuration);

// MQTT transport (ARV-024), off unless Ingest:Mqtt:Enabled: a broker on its own TLS listener that takes device data in
// through the same ingest, with the device credential, a topic per device and no subscriptions.
builder.Services.AddArivaMqttTransport(builder.Configuration, environment);
builder.WebHost.UseArivaMqttListener();

// AMAN: AddAppCaching, AddAppHealthChecks, AddAppRouting, AddAppOpenApi.
// Adapters: one poller per vendor registered as a hosted service; a Kafka producer per normalised topic.

#endregion

var app = builder.Build();

#region Middlewares

// AMAN order (UseAppForwardedHeaders, UseErrorHandling, UseRouting, UseRequestTracking, UseAppCors,
// UseAuthentication, UseSessionContext, UseAuthorization) with the security headers and rate limiting added.
// First of all: the moment a request arrived is its receipt time (F19), before authentication and reading the body, so a
// slow first credential check never looks like the device's clock running behind (ARV-064).
app.UseReceiptTime();
// Before the forwarded headers: the certificate header is trusted only from a proxy, while the address is still the proxy's.
app.UseAppClientCertificates(builder.Configuration);
app.UseAppForwardedHeaders();
app.UseAppSecurityHeaders();
app.UseErrorHandling();
app.UseRouting();
// AMAN: UseRequestTracking
app.UseAppCors();
app.UseAppRateLimiting();
app.UseAuthentication();
app.UseSessionValidation();
app.UsePendingScope();
// AMAN: UseSessionContext
app.UseAuthorization();

#endregion

#region Endpoints

app.MapArivaHealthChecks();
app.MapControllers();
app.MapArivaOpenApi(app.Configuration);

// AMAN: MapOpenApi, MapScalarApiReference. Vendor webhook endpoints (Endpoints/) are mapped here.

#endregion

#region Bootstrap

// AMAN: await IBootstrapper.Initialize();

#endregion

await app.RunAsync();
