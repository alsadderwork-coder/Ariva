using Ariva.Api.Common.Filters;
using Ariva.Api.Common.Security;
using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.HealthChecks;
using Ariva.Api.Common.Hosting;
using Ariva.Di;
using Ariva.Di.Extensions;

// Ariva.Api.Integration: AODB adapters (Aodb/: AIDX, ACRIS, SSIM), the AMAN aggregate feed
// consumer (Aman/) and email notifications (Notifications/).
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
builder.Services.AddAppTelemetry(builder.Configuration);
// AMAN: builder.Host.AddAppHosting(); builder.WebHost.AddAppWebHost(...);

#endregion

#region Services

// AMAN feed (ARV-048): the four aman.feed.*.v1 topics, when Kafka is on; the same records also arrive over REST.
builder.Services.RegisterArivaServices(builder.Configuration, messaging => messaging.ConsumeAmanFeed());
builder.Services.AddArivaBorderFeed(builder.Configuration);

// Email notifications (ARV-040): the alert emails the other hosts write with each alert change are sent from here, the
// host with the outbound egress (the site's mail relay), through MailKit with the Email settings.
builder.Services.AddArivaEmailSending(builder.Configuration);

// Flights (ARV-041): every flight feed (Integration API, AIDX, ACRIS, SSIM) enters the model here, and the stale-feed
// alarm watches the feeds of each site.
builder.Services.AddArivaFlights(builder.Configuration);

// Security baseline (docs/security/cwe-controls.md): default deny (fallback policy, Ariva.Deny answers challenges),
// ES256 access tokens from Ariva.Api.Main with permissions from stored grants (ARV-010a), input limits, rate limiting,
// ProblemDetails errors, trusted forwarded headers and the CORS allow-list.
builder.Services.AddAppSecurityBaseline(builder.Configuration);
builder.Services.AddAppControllers();

// Integration clients (ARV-042): the token exchange (POST /api/v1/auth) and the integration scheme, with its own key
// ring and audience, that every Integration API endpoint ([IntegrationScope]) accepts and nothing else does.
builder.Services.AddAuthentication().AddArivaIntegrationAuthentication(builder.Configuration);
builder.Services.AddArivaIntegrationAuth();
builder.Services.AddArivaIntegrationBatches();
builder.Services.AddArivaOutboundCalls(builder.Configuration, builder.Environment.EnvironmentName);
builder.Services.AddSingleton<Ariva.Api.Common.Security.IntegrationClientRateLimiter>();

// AMAN: AddAppCaching, AddAppHealthChecks, AddAppRouting, AddAppOpenApi.

#endregion

var app = builder.Build();

#region Middlewares

// AMAN order (UseAppForwardedHeaders, UseErrorHandling, UseRouting, UseRequestTracking, UseAppCors,
// UseAuthentication, UseSessionContext, UseAuthorization) with the security headers and rate limiting added.
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
// Every Integration API call is recorded (client, scope, route, site, answer, payload SHA-256), refused ones too.
app.UseIntegrationCallAudit();
// AMAN: UseSessionContext
app.UseAuthorization();

#endregion

#region Endpoints

app.MapArivaHealthChecks();
app.MapControllers();

// AMAN: MapOpenApi, MapScalarApiReference. AODB push endpoints are mapped here.

#endregion

#region Bootstrap

// AMAN: await IBootstrapper.Initialize();

#endregion

await app.RunAsync();
