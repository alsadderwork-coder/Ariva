using Ariva.Api.Common.Hubs;
using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Filters;
using Ariva.Api.Common.Security;
using Ariva.Di.Extensions;
using Ariva.Api.Common.HealthChecks;
using Ariva.Api.Common.Hosting;
using Ariva.Di;

// Ariva.Api.Main: configuration and operations REST API, and later the SignalR live hub.
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
// --migrate (ARV-006) is handled below and never reaches the configuration command line provider.
var migrate = DatabaseMigration.IsRequested(args);
args = DatabaseMigration.WithoutFlag(args);
// --provision-topics (ARV-062): the Helm topics Job's command, never configuration.
var provisionTopics = TopicProvisioning.IsRequested(args);
args = TopicProvisioning.WithoutFlag(args);
// --create-break-glass / --rotate-break-glass (ARV-010c) are installer commands, never configuration.
var breakGlassArgs = args;
var breakGlass = BreakGlassCommand.IsRequested(args);
args = BreakGlassCommand.WithoutFlags(args);
// --check-stored-names (ARV-159): the read-only pre-pilot check of stored names, never configuration.
var checkStoredNames = StoredNameCheckCommand.IsRequested(args);
args = StoredNameCheckCommand.WithoutFlag(args);

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
// ARV-098: the settings must name this host's own environment, or the host does not start.
ArivaEnvironment.EnsureConfigured(builder.Configuration, environment);

#endregion

#region Migration and topic jobs

// One-shot schema migration: the Helm migration Job starts this image with --migrate before each release.
if (migrate)
{
    return await DatabaseMigration.RunAsync(builder.Configuration);
}

// One-shot topic provisioning: the Helm topics Job starts this image with --provision-topics before each release.
if (provisionTopics)
{
    return await TopicProvisioning.RunAsync(builder.Configuration);
}

// Installer command: the deployment's break-glass account, printed once (ARV-010c).
if (breakGlass)
{
    return await BreakGlassCommand.RunAsync(builder.Configuration, builder.Environment.EnvironmentName, breakGlassArgs, Console.Out);
}

// Operator command: lists stored names that the name rule refuses, read-only, before a pilot (ARV-159).
if (checkStoredNames)
{
    return await StoredNameCheckCommand.RunAsync(builder.Configuration, Console.Out);
}

#endregion

#region Logging, Hosting and Telemetry

// Serilog with secret redaction, JSON console, Loki and OTLP by configuration; traces and metrics over OTLP (ARV-007).
builder.AddAppLogging();
builder.AddAppServiceDefaults();
// AMAN: builder.Host.AddAppHosting(); builder.WebHost.AddAppWebHost(...);

#endregion

#region Services

// Main consumes device health reports into heartbeats (ARV-025).
builder.Services.RegisterArivaServices(builder.Configuration, messaging => messaging.ConsumeDeviceHealth());
// Main runs the validation service (ARV-104g1): the only host allowed the validation reader login outside vm-local.
builder.Services.AddArivaValidationReaderHost();

// Security baseline (docs/security/cwe-controls.md): default deny (fallback policy, Ariva.Deny answers challenges),
// ES256 access tokens from Ariva.Api.Main with permissions from stored grants (ARV-010a), input limits, rate limiting,
// ProblemDetails errors, trusted forwarded headers and the CORS allow-list.
builder.Services.AddAppSecurityBaseline(builder.Configuration, builder.Environment.EnvironmentName);
// Controller input is read strictly (ARV-089). TEMPORARY: the login freeze keeps the sign-in, password, TOTP and step-up
// endpoints on the lenient parsing they had before; ARV-089a removes this exemption.
builder.Services.AddAppControllers()
    .WithLenientInput<Ariva.Api.Main.Controllers.Auth.AuthController>();
// OpenAPI document for the dynamic scan (ARV-063): only with OpenApi:Enabled, administrators only, refused in k8s-prd.
builder.Services.AddArivaOpenApi(builder.Configuration, builder.Environment.EnvironmentName);

// Ariva.Api.Main signs users in and issues tokens (ADR-0026); the other hosts only validate them.
builder.Services.AddArivaTokenIssuing(builder.Configuration, builder.Environment.EnvironmentName);

// Validation results (ARV-104g2 service, ARV-104g endpoints): settings, the host-wide single-flight of computations, the
// site-scoped service, the background freeze of closed campaigns and the results' request timeout policy.
builder.Services.AddArivaValidationResults(builder.Configuration);
// Request timeouts (ARV-096): a default for every endpoint without its own policy, and the reports and transfer policies;
// Main only (Ariva.Api.Common/Settings/RequestTimeoutsSettings.cs says why the other hosts set none).
builder.Services.AddAppRequestTimeouts(builder.Configuration);

// Integration clients (ARV-042): administrators register the systems that call the Integration API.
builder.Services.AddArivaIntegrationClients();
builder.Services.AddArivaOutboundEndpoints(builder.Configuration, builder.Environment.EnvironmentName);
builder.Services.AddArivaFlightSchedules(builder.Configuration);
builder.Services.AddArivaArrivalWave(builder.Configuration);
// ARV-181a: the indicative staffing recommendation Ariva.Api.Cronz stores each minute (border data, ArrivalWaveLanes.View).
builder.Services.AddArivaStaffingRecommendations(builder.Configuration);

// Demo Airport (DMO) topology and zone profile v12 in vm-local and k8s-demo; refused outside dev and demo (ARV-019).
builder.Services.AddArivaDemoSeed(builder.Configuration, builder.Environment.EnvironmentName);

// Device heartbeats (ARV-025): a commissioned device not heard from for Devices:Health:HeartbeatTimeoutSeconds goes
// Offline and its queue zone Degraded; one sweep at a time across replicas.
builder.Services.AddArivaDeviceHealthMonitor();

// AMAN: AddAppCaching, AddAppHealthChecks, AddAppRouting, AddAppOpenApi, AddAppSignalR.
// SignalR (ARV-035): the live queue hub with session checks (ADR-0026), MessagePack beside JSON, bounded messages, and
// the Redis backplane when Redis is configured; the relay forwards Stream's live snapshots to the zone groups.
var signalR = builder.Services.AddSignalR(options =>
    {
        options.MaximumReceiveMessageSize = 8 * 1024;
        options.EnableDetailedErrors = false;
        options.MaximumParallelInvocationsPerClient = 1;
    })
    .AddMessagePackProtocol()
    .AddArivaHubSessions();
var redisSettings = Ariva.Infra.Caching.RedisSettings.From(builder.Configuration);
if (redisSettings.Enabled)
    signalR.AddStackExchangeRedis(options =>
    {
        // The cache's options (ARV-082a: TLS pinned to the internal CA in production), on a connection and channel prefix of the hub's own.
        options.Configuration = redisSettings.Options();
        options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal(redisSettings.InstanceName + "signalr");
    });
builder.Services.AddScoped<Ariva.Infra.Live.LiveZoneDirectory>();
builder.Services.AddHostedService<Ariva.Api.Main.Hubs.LiveRelay>();

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
// Request timeouts after routing and authorization (ARV-096): every endpoint gets the default policy unless it names its own
// ([RequestTimeout]: the reports, the file transfers, the validation results) or is exempt (the live hub, below).
app.UseRequestTimeouts();
// The live hub's total per replica (ARV-096b): a negotiate is answered 429 once the replica holds Security:Hubs:MaxConnections.
app.UseHubConnectionLimit(Ariva.Api.Main.Hubs.LiveHub.Path);

#endregion

#region Endpoints

app.MapArivaHealthChecks();
app.MapControllers();
app.MapArivaOpenApi(app.Configuration);
// The hub's [Permission] and RequireAuthorization both apply, so the hub stays closed even without its attribute. Its
// connections last as long as a screen is open, so the default request timeout would cut them: exempt (ARV-096).
app.MapHub<Ariva.Api.Main.Hubs.LiveHub>(Ariva.Api.Main.Hubs.LiveHub.Path, Ariva.Api.Main.Hubs.LiveHub.Configure).RequireAuthorization()
    .DisableRequestTimeout();

// AMAN: MapOpenApi, MapScalarApiReference. Controllers live in Controllers/AdminArea/<Entity>/Controller.cs
// and Controllers/OpsArea/<Entity>/Controller.cs; hubs are mapped here with MapHub and RequireAuthorization.

#endregion

#region Bootstrap

// AMAN: await IBootstrapper.Initialize();

#endregion

await app.RunAsync();
return 0;
