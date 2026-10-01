using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ariva.Core;
using Ariva.Core.Security;
using Ariva.Core.Services.Security;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-009: security/permission-matrix.json is the single list of endpoints and the status each caller gets. These tests
/// fail when an endpoint is missing from it, when a permission row disagrees with the role seed, or when a host
/// answers a row differently. The E2E suite reads the same file.
/// </summary>
[Collection(HostCollection.Name)]
public sealed partial class PermissionMatrixTests
{
    private const string Anonymous = "anonymous";

    private static readonly Lazy<Matrix> Loaded = new(() =>
        JsonSerializer.Deserialize<Matrix>(File.ReadAllText(RepositoryPaths.Resolve("security/permission-matrix.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));

    private static readonly Lazy<HashSet<string>> CriticalRoutes = new(() =>
        JsonDocument.Parse(File.ReadAllText(RepositoryPaths.Resolve("security/critical-actions.json"))).RootElement.GetProperty("routes")
            .EnumerateArray()
            .Select(r => $"{r.GetProperty("host").GetString()} {r.GetProperty("method").GetString()} {r.GetProperty("route").GetString()}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    public static TheoryData<string> HostsInMatrix => [.. ArivaHosts.All];

    [Fact]
    public void Roles_Should_MatchRoleCodes_When_MatrixIsRead()
    {
        Loaded.Value.Roles.Should().BeEquivalentTo(RoleCodes.All);
        Loaded.Value.Endpoints.Should().AllSatisfy(row =>
            row.Expected.Keys.Should().BeEquivalentTo([Anonymous, .. RoleCodes.All], $"{row} needs a status for every caller"));
    }

    [Fact]
    public void PermissionRows_Should_AgreeWithRoleSeed_When_MatrixIsRead()
    {
        var problems = new List<string>();
        foreach (var row in Loaded.Value.Endpoints)
        {
            switch (row.Access)
            {
                case "anonymous":
                    break;
                case "authenticated":
                    Expect(row, Anonymous, 401, problems);
                    break;
                case "permission":
                    Expect(row, Anonymous, 401, problems);
                    var permissions = (row.Permissions ?? []).Select(Global.Defaults.Permissions.Find).ToList();
                    if (permissions.Count == 0 || permissions.Any(p => p is null))
                    {
                        problems.Add($"{row}: unknown or missing permission names");
                        continue;
                    }

                    var critical = CriticalRoutes.Value.Contains($"{row.Host} {row.Method} {row.Route}");
                    foreach (var role in RoleCodes.All)
                    {
                        var granted = permissions.Any(RolePermissions.ByRole[role].Contains);
                        // A critical action answers 401 mfa_required to a holder without a recent second factor
                        // (ARV-010d); the matrix callers never have one.
                        if (granted && critical && row.Expected[role] != 401)
                            problems.Add($"{row}: critical, so {role} without a recent second factor must get 401, the matrix says {row.Expected[role]}");
                        if (granted && !critical && row.Expected[role] is 401 or 403)
                            problems.Add($"{row}: {role} holds {string.Join("/", row.Permissions)} but the matrix expects {row.Expected[role]}");
                        if (!granted)
                            Expect(row, role, 403, problems);
                    }

                    break;
                default:
                    problems.Add($"{row}: access must be anonymous, authenticated or permission");
                    break;
            }
        }

        problems.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(HostsInMatrix))]
    public async Task GetEndpoints_Should_AllBeListed_When_HostIsBooted(string host)
    {
        await using var app = ArivaHosts.Create(host);

        var mapped = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => $"{method} /{endpoint.RoutePattern.RawText?.TrimStart('/')}"))
            .ToList();
        var listed = RowsFor(host).Select(row => $"{row.Method} {row.Route}").ToHashSet(StringComparer.OrdinalIgnoreCase);

        mapped.Should().NotBeEmpty();
        mapped.Where(endpoint => !listed.Contains(endpoint)).Should().BeEmpty("security/permission-matrix.json must list every endpoint");
    }

    [Theory]
    [MemberData(nameof(HostsInMatrix))]
    public async Task Send_Should_ReturnMatrixStatus_When_EachCallerCallsEachEndpoint(string host)
    {
        // Every caller shares one client address in-process; the sign-in limit (10 a minute for login, refresh and
        // password change together) is tested in RateLimitingAndCorsTests, not here.
        await using var app = ArivaHosts.Create(host, configure: builder => builder
            .UseSetting("Security:RateLimiting:Auth:PermitLimit", "1000")
            .ConfigureTestServices(services =>
        {
            TestAuthenticationHandler.Register(services);
            // No database in-process: the sign-in service fails every call, as the real one does for the matrix bodies.
            services.Replace(ServiceDescriptor.Scoped<ISvcAuthenticator, FakeAuthenticator>());
            FakeAdministration.Register(services);
        }));
        using var client = app.CreateClient();
        var mismatches = new List<string>();

        foreach (var row in RowsFor(host))
        {
            foreach (var (caller, expected) in row.Expected)
            {
                using var request = new HttpRequestMessage(new HttpMethod(row.Method), PathOf(row));
                if (row.Body is { } body)
                    request.Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");
                if (caller != Anonymous)
                {
                    request.Headers.Add(TestAuthenticationHandler.UserHeader, "matrix-" + caller);
                    request.Headers.Add(TestAuthenticationHandler.RolesHeader, caller);
                }

                using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
                if ((int)response.StatusCode != expected)
                    mismatches.Add($"{row} as {caller}: expected {expected}, got {(int)response.StatusCode}");
            }
        }

        mismatches.Should().BeEmpty();
    }

    /// <summary>The route template with its parameters filled from routeValues ({id:guid} becomes routeValues.guid, {siteCode} routeValues.siteCode).</summary>
    private static string PathOf(MatrixRow row) =>
        RouteParameter().Replace(row.Route, match => Loaded.Value.RouteValues[match.Groups["type"].Success ? match.Groups["type"].Value : match.Groups["name"].Value]);

    [GeneratedRegex(@"\{(?<name>[a-zA-Z]+)(:(?<type>[a-z]+))?\}")]
    private static partial Regex RouteParameter();

    private static IEnumerable<MatrixRow> RowsFor(string host) =>
        Loaded.Value.Endpoints.Where(row => string.Equals(row.Host, host, StringComparison.OrdinalIgnoreCase));

    private static void Expect(MatrixRow row, string caller, int status, List<string> problems)
    {
        if (row.Expected.TryGetValue(caller, out var actual) && actual != status)
            problems.Add($"{row}: {caller} must get {status}, the matrix says {actual}");
    }

    private sealed record Matrix(List<string> Roles, Dictionary<string, string> RouteValues, List<MatrixRow> Endpoints);

    private sealed record MatrixRow(string Host, string Method, string Route, string Access, List<string> Permissions, JsonElement? Body, Dictionary<string, int> Expected)
    {
        public override string ToString() => $"{Host} {Method} {Route}";
    }
}
