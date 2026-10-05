using Ariva.Api.Common.Security;
using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.HealthChecks;
using Ariva.Api.Common.Hosting;
using Ariva.Di;
using Ariva.Di.Extensions;

// Ariva.Api.Stream: Kafka stream workers (Workers/) that compute queue state from normalised
// sensor, flight and AMAN feeds. HTTP is used for health probes only.
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
// --replay and --verify-replay (ARV-036) are commands, never configuration.
var replayArgs = args;
var replay = ReplayCommand.IsRequested(args);
args = ReplayCommand.WithoutFlags(args);

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

#region Replay command

// Golden replay over the archive, or the check of an exported replay (ARV-036); the host itself does not start.
if (replay)
{
    return await ReplayCommand.RunAsync(builder.Configuration, replayArgs, Console.Out);
}

#endregion

#region Logging, Hosting and Telemetry

// Serilog with secret redaction, JSON console, Loki and OTLP by configuration; traces and metrics over OTLP (ARV-007).
builder.AddAppLogging();
builder.AddAppServiceDefaults();
// AMAN: builder.Host.AddAppHosting(); builder.WebHost.AddAppWebHost(...);

#endregion

#region Services

// Stream archives every accepted sensing event for replay and recomputation (ARV-026).
builder.Services.RegisterArivaServices(builder.Configuration, messaging => messaging.ConsumeSensingArchive().StreamQueueZones());
builder.Services.AddArivaSensingArchive();

// The queue engine worker (ARV-034): the sensing topics into zone engines, minute rows, bins and snapshots.
builder.Services.AddArivaQueueStream(builder.Configuration);

// The live alert evaluation (ARV-038): every enabled rule judged each minute on the stored minutes; raises and clears.
builder.Services.AddArivaAlertEvaluation(builder.Configuration);

// The desk feed (ARV-049): AMAN's stored desk sessions and intervals into the desk state engine, and e-gate minutes.
builder.Services.AddArivaDeskFeed(builder.Configuration);

// Security baseline (docs/security/cwe-controls.md): default deny (fallback policy, Ariva.Deny answers challenges),
// ES256 access tokens from Ariva.Api.Main with permissions from stored grants (ARV-010a), input limits, rate limiting,
// ProblemDetails errors, trusted forwarded headers and the CORS allow-list.
builder.Services.AddAppSecurityBaseline(builder.Configuration, builder.Environment.EnvironmentName);

// AMAN: AddAppCaching, AddAppHealthChecks.

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
// AMAN: UseSessionContext
app.UseAuthorization();

#endregion

#region Endpoints

app.MapArivaHealthChecks();

// No API surface: this host maps health probes only.

#endregion

#region Bootstrap

// AMAN: await IBootstrapper.Initialize();

#endregion

await app.RunAsync();
return 0;
