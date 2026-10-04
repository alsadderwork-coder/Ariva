using Aspire.Hosting.ApplicationModel;
using Ariva.AppHost;

// Ariva.AppHost (ARV-066): the local orchestration of the whole system, mirroring docker-compose.dev.yml plus the hosts.
// Every host runs in vm-local, as `dotnet run` would, with its dependencies passed as the configuration parts it already
// reads (Database:*, Kafka:*, Redis:*, Email:*): no host learns anything Aspire-specific beyond OTEL_EXPORTER_OTLP_*.

var builder = DistributedApplication.CreateBuilder(args);
var settings = AppHostSettings.From(builder.Configuration);
var repository = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", ".."));
var hostEnvironment = settings.ReadHostEnvironment();

#region Secrets

// Generated on first run and kept in this project's user secrets (persist), never in the repository.
static GenerateParameterDefault Password() => new() { MinLength = 32, Special = false };
var ownerPassword = builder.AddParameter("database-owner-password", Password(), secret: true, persist: true);
var runtimePassword = builder.AddParameter("database-runtime-password", Password(), secret: true, persist: true);
var readonlyPassword = builder.AddParameter("database-readonly-password", Password(), secret: true, persist: true);
var redisPassword = builder.AddParameter("redis-password", Password(), secret: true, persist: true);
// The simulator's operator key (Bearer, read and control scopes): shown in the dashboard's parameters; the simulator
// keeps only its SHA-256, as in every environment (ARV-027).
var simulatorKey = builder.AddParameter("simulation-operator-key", Password(), secret: true, persist: true);

#endregion

#region Containers (as docker-compose.dev.yml)

// The migration login owns the schema (ARV-006); the hosts create the DML-only runtime login at startup in vm-local.
var timescale = builder.AddPostgres("timescaledb", builder.AddParameter("database-owner", "ariva"), ownerPassword, settings.DatabasePort)
    .WithImageRegistry("docker.io")
    .WithImage("timescale/timescaledb-ha", "pg17-ts2.30")
    .WithEnvironment("POSTGRES_DB", "ariva")
    .WithEnvironment("TIMESCALEDB_TELEMETRY", "off")
    .WithEnvironment("ARIVA_READONLY_PASSWORD", readonlyPassword)
    // The read-only role for the postgres-dev MCP (deploy/local/postgres-init), run once on a new data directory.
    .WithInitFiles(Path.Combine(repository, "deploy", "local", "postgres-init"))
    .WithLifetime(settings.Persistent ? ContainerLifetime.Persistent : ContainerLifetime.Session);
if (!string.IsNullOrWhiteSpace(settings.DatabaseVolume))
{
    // The HA image keeps its data under /home/postgres/pgdata, not the official image's path.
    timescale.WithVolume(settings.DatabaseVolume, "/home/postgres/pgdata");
}

var kafka = builder.AddKafka("kafka", settings.KafkaPort);

var redis = builder.AddRedis("redis", settings.RedisPort, redisPassword);

var smtp = builder.AddContainer("smtp4dev", "rnwood/smtp4dev", "3.15.0")
    .WithImageRegistry("docker.io")
    .WithHttpEndpoint(port: settings.SmtpUiPort, targetPort: 80, name: "http")
    .WithEndpoint(port: settings.SmtpPort, targetPort: 25, name: "smtp", scheme: "tcp");

#endregion

#region Hosts

var database = timescale.Resource.PrimaryEndpoint;
var broker = kafka.Resource.PrimaryEndpoint;
var mail = smtp.GetEndpoint("smtp");

IResourceBuilder<ProjectResource> Host<TProject>(string name) where TProject : IProjectMetadata, new()
{
    var host = Ariva(builder.AddProject<TProject>(name))
        .WithEnvironment("Database__Host", ReferenceExpression.Create($"{database.Property(EndpointProperty.Host)}"))
        .WithEnvironment("Database__Port", ReferenceExpression.Create($"{database.Property(EndpointProperty.Port)}"))
        .WithEnvironment("Database__Name", "ariva")
        .WithEnvironment("Database__Username", "ariva_app")
        .WithEnvironment("Database__Password", runtimePassword)
        .WithEnvironment("Database__Migration__Username", "ariva")
        .WithEnvironment("Database__Migration__Password", ownerPassword)
        .WithEnvironment("Kafka__Enabled", "true")
        .WithEnvironment("Kafka__BootstrapServers", ReferenceExpression.Create($"{broker.Property(EndpointProperty.Host)}:{broker.Property(EndpointProperty.Port)}"))
        .WithEnvironment("Kafka__ProvisionTopics", "true")
        .WithEnvironment("Kafka__Topics__ReplicationFactor", "1")
        .WithEnvironment("Kafka__Topics__MinInSyncReplicas", "1")
        .WithEnvironment("Redis__Enabled", "true")
        .WithEnvironment("Redis__ConnectionString", redis)
        .WithEnvironment("Email__Enabled", "true")
        .WithEnvironment("Email__FromAddress", "no-reply@ariva.local")
        .WithEnvironment("Email__Smtp__Host", ReferenceExpression.Create($"{mail.Property(EndpointProperty.Host)}"))
        .WithEnvironment("Email__Smtp__Port", ReferenceExpression.Create($"{mail.Property(EndpointProperty.Port)}"))
        .WithEnvironment("Email__Smtp__Security", "None")
        .WithEnvironment("Email__Smtp__AllowInsecure", "true")
        // Ready means the host's own dependencies answer (HealthExtensions), not just that it listens.
        .WithHttpHealthCheck("/health/readiness");
    return Overrides(host, name);
}

// What every Ariva process gets, after the variables Aspire adds to a project (later callbacks win).
static IResourceBuilder<ProjectResource> Ariva(IResourceBuilder<ProjectResource> project) => project
    // Ariva's own environment names only (ArivaEnvironment refuses Development). Variables, not an argument: projects are
    // started through dotnet run, which would take --environment as its own option.
    .WithEnvironment("DOTNET_ENVIRONMENT", "vm-local")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "vm-local")
    // Aspire turns URL query redaction off for its dashboard; Ariva keeps it on everywhere, because the SignalR hub
    // carries access_token in the query (TelemetryExtensions, CWE-532).
    .WithEnvironment("OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION", "false")
    .WithEnvironment("OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION", "false");

// The E2E suite's settings for this resource, applied last (AppHost:HostEnvironmentFile).
IResourceBuilder<T> Overrides<T>(IResourceBuilder<T> resource, string name) where T : IResourceWithEnvironment
{
    if (hostEnvironment.TryGetValue(name, out var variables))
    {
        foreach (var (key, value) in variables)
            resource.WithEnvironment(key, value);
    }

    return resource;
}

// Main migrates the schema at startup in vm-local; the others start once it is ready, so only one host migrates.
var main = Host<Projects.Ariva_Api_Main>("api-main").WaitFor(timescale).WaitFor(kafka).WaitFor(redis).WaitFor(smtp);
var ingest = Host<Projects.Ariva_Api_Ingest>("api-ingest").WaitFor(main);
if (settings.Stream)
    Host<Projects.Ariva_Api_Stream>("api-stream").WaitFor(main);
Host<Projects.Ariva_Api_Cronz>("api-cronz").WaitFor(main);
var integration = Host<Projects.Ariva_Api_Integration>("api-integration").WaitFor(main);

// The simulator sends sensor pushes to Ingest, AODB and AMAN feeds to Integration and AMAN's Kafka feed (the AMAN mimic)
// to the same broker; it never touches the database.
Overrides(Ariva(builder.AddProject<Projects.Ariva_Simulation_Api>("simulation"))
        .WithEnvironment("Simulation__Aman__Kafka__BootstrapServers", ReferenceExpression.Create($"{broker.Property(EndpointProperty.Host)}:{broker.Property(EndpointProperty.Port)}"))
        .WithEnvironment("Simulation__Control__Keys__0__Name", "apphost")
        .WithEnvironment(async context =>
        {
            var key = await simulatorKey.Resource.GetValueAsync(context.CancellationToken);
            context.EnvironmentVariables["Simulation__Control__Keys__0__Sha256"] =
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key ?? string.Empty)));
        })
        .WithEnvironment("Simulation__Control__Keys__0__Scopes__0", "read")
        .WithEnvironment("Simulation__Control__Keys__0__Scopes__1", "control")
        .WithHttpHealthCheck("/health/readiness")
        .WaitFor(ingest)
        .WaitFor(integration),
    "simulation");

if (settings.Web)
{
    // The SvelteKit dev server on its usual port (Main allows that origin for the refresh cookie, vm-local CORS), its
    // /api and /hubs proxied to Main. Packages come from npm ci, never installed here.
    builder.AddViteApp("web", Path.Combine(repository, "Platform", "Frontplane", "Ariva.Web"), "dev:apphost")
        .WithNpm(install: false)
        .WithEndpoint("http", endpoint =>
        {
            endpoint.Port = 51010;
            endpoint.TargetPort = 51010;
            endpoint.IsProxied = false;
        })
        .WithEnvironment("ARIVA_WEB_API_PROXY", main.GetEndpoint("http"))
        .WaitFor(main);
}

#endregion

builder.Build().Run();
