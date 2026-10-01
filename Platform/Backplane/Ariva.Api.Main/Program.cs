using Ariva.Api.Common.Extensions;
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
    .AddEnvironmentVariables()
    .AddCommandLine(args);

#endregion

#region Logging, Hosting and Telemetry

// AMAN: builder.Logging.AddAppLogging(...); builder.Host.AddAppHosting();
//       builder.WebHost.AddAppWebHost(...); builder.Services.AddAppTelemetry(...);

#endregion

#region Services

builder.Services.RegisterArivaServices(builder.Configuration);

// Security baseline (docs/security/cwe-controls.md): default deny with the Ariva.Deny placeholder scheme, input
// limits, rate limiting, ProblemDetails errors, trusted forwarded headers and the CORS allow-list. The
// authentication story replaces the placeholder scheme with the JWT bearer schemes.
builder.Services.AddAppSecurityBaseline(builder.Configuration);
builder.Services.AddControllers();

// AMAN: AddAppCaching, AddAppHealthChecks, AddAppRouting, AddAppOpenApi, AddAppSignalR.
// SignalR: the live queue hub (Hubs/) uses the Redis backplane and the MessagePack protocol.

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
// AMAN: UseSessionContext
app.UseAuthorization();

#endregion

#region Endpoints

app.MapArivaHealthChecks();
app.MapControllers();

// AMAN: MapOpenApi, MapScalarApiReference. Controllers live in Controllers/AdminArea/<Entity>/Controller.cs
// and Controllers/OpsArea/<Entity>/Controller.cs; hubs are mapped here with MapHub and RequireAuthorization.

#endregion

#region Bootstrap

// AMAN: await IBootstrapper.Initialize();

#endregion

await app.RunAsync();
