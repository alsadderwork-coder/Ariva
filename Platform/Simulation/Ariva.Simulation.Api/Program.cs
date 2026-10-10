using Ariva.ServiceDefaults;
using Ariva.Simulation.Api.Emulators;
using Ariva.Simulation.Api.Emulators.Sensors;
using Ariva.Simulation.Api.Emulators.Validation;
using Ariva.Simulation.Api.Security;

// Ariva.Simulation.Api: sensor, AODB and AMAN emulators and validation observers (Emulators/) driven by deterministic
// scenarios (Scenarios/). This host references Ariva.Business.Contracts only, so it emits AMAN feed messages exactly
// as AMAN would. It exposes the health probes used by the Helm chart and the scenario endpoints (ARV-027), which
// need an operator key (Simulation:Control).

#region Configuration

// Same layering as the Backplane hosts: base, base per environment, service, service per environment,
// then environment variables and command line arguments. The environment comes from --environment,
// DOTNET_ENVIRONMENT or ASPNETCORE_ENVIRONMENT, then environment.json, and is also the host environment name.
// ARV-098: no default and no unknown name (SimulationEnvironment).
var environment = SimulationEnvironment.Resolve(args, AppContext.BaseDirectory);

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

// LogFile:Path, as Ariva's hosts honour it: the E2E run's log scan reads the simulator's lines too (CWE-532).
SimulationLogFile.AddTo(builder.Logging, builder.Configuration);

#endregion

#region Production guard

// The simulator fabricates sensor, AODB and AMAN data. It must never run in production, whatever the Helm values
// say (simulationEnabled is false in values-k8s-prd.yaml); refuse to start and exit non-zero.
if (string.Equals(environment, SimulationSecurity.ProductionEnvironment, StringComparison.OrdinalIgnoreCase)
    || string.Equals(builder.Configuration["Application:Environment"], SimulationSecurity.ProductionEnvironment, StringComparison.OrdinalIgnoreCase))
{
    using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
    loggerFactory
        .CreateLogger("Ariva.Simulation.Api")
        .LogCritical("Ariva.Simulation.Api refuses to start in {Environment}: the simulator must never run in production.",
            SimulationSecurity.ProductionEnvironment);
    return 1;
}

#endregion

#region Services

// Traces, metrics and logs over OTLP when an endpoint is configured (the Aspire AppHost sets one), and resilience for
// the emulators' HttpClients: POSTs are never retried, so no sensor event or feed message is sent twice (ARV-066).
builder.AddArivaServiceDefaults(options =>
{
    options.ServiceName = "ariva-simulation";
    options.ExportLogs = true;
});
builder.Services.AddSimulationSecurity(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Ariva.Simulation.Api.Scenarios.ScenarioEngine>();
builder.Services.AddFeedEmulators(builder.Configuration);
builder.Services.AddSensorEmulator(builder.Configuration);
// ARV-104i: the validation observers, who sign in to Ariva.Api.Main as Validation observer accounts and use its capture API.
builder.Services.AddValidationEmulator(builder.Configuration);
// Binding errors say what was wrong, not which internal type failed to bind (CWE-209), and never repeat a value or a member
// name sent (CWE-501, ARV-104i: as Ariva's hosts since ARV-104b).
builder.Services.AddControllers(SimulationSecurity.ConfigureBinding)
    .AddJsonOptions(options =>
    {
        options.AllowInputFormatterExceptionMessages = false;
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(allowIntegerValues: false));
    })
    .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = SimulationSecurity.ProblemWithoutBodyPaths);

#endregion

var app = builder.Build();

// Run every scenario site's day now (DMO and AUH-TA, ARV-139b), not on the first request: an invalid seed stops the host at start.
app.Services.GetRequiredService<Ariva.Simulation.Api.Scenarios.ScenarioEngine>();

#region Middlewares

app.UseSimulationSecurity();

#endregion

#region Endpoints

// Health probes are the only anonymous endpoints (allowlisted for SEC-052 in security/allowlist.json); everything
// else, including the emulator controllers added by the backlog, requires authorization through the fallback policy.
app.MapGet("/health/startup", () => Results.Ok(new { status = "Healthy", probe = "startup" })).AllowAnonymous().ExcludeFromDescription();
app.MapGet("/health/readiness", () => Results.Ok(new { status = "Healthy", probe = "readiness" })).AllowAnonymous().ExcludeFromDescription();
app.MapGet("/health/liveness", () => Results.Ok(new { status = "Healthy", probe = "liveness" })).AllowAnonymous().ExcludeFromDescription();
app.MapControllers();

#endregion

await app.RunAsync();

return 0;
