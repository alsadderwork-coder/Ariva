using Ariva.Di.Extensions;
using Ariva.Infra.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ariva.Di;

/// <summary>
/// The installer command for the break-glass account (ARV-010c). Ariva.Api.Main runs it instead of the web host when
/// started with <c>--create-break-glass</c> or <c>--rotate-break-glass</c>. The credential (username, password, ten
/// recovery codes) is printed once to standard output, or written to the file named by
/// <c>--break-glass-output=&lt;path&gt;</c> with owner-only permissions; it is never logged. Seal it and store it offline.
/// </summary>
public static class BreakGlassCommand
{
    public const string CreateFlag = "--create-break-glass";
    public const string RotateFlag = "--rotate-break-glass";
    public const string OutputPrefix = "--break-glass-output=";

    public static bool IsRequested(string[] args) =>
        args?.Any(a => a is CreateFlag or RotateFlag) == true;

    /// <summary>The arguments without this command's flags, so the configuration command line provider never sees them.</summary>
    public static string[] WithoutFlags(string[] args) =>
        (args ?? []).Where(a => a is not (CreateFlag or RotateFlag) && !a.StartsWith(OutputPrefix, StringComparison.Ordinal)).ToArray();

    /// <summary>Issues the credential and returns the process exit code: 0 on success, 1 on failure (logged).</summary>
    public static async Task<int> RunAsync(IConfiguration configuration, string hostEnvironment, string[] args, TextWriter output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(output);

        var rotate = args.Contains(RotateFlag, StringComparer.Ordinal);
        var outputPath = args.FirstOrDefault(a => a.StartsWith(OutputPrefix, StringComparison.Ordinal))?[OutputPrefix.Length..];

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddSimpleConsole(options => options.SingleLine = true));
        services.AddArivaPersistence(configuration);
        services.AddArivaAccounts(configuration, hostEnvironment);
        services.AddScoped<BreakGlassAccounts>();

        await using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(BreakGlassCommand));
        try
        {
            await using var scope = provider.CreateAsyncScope();
            var credential = await scope.ServiceProvider.GetRequiredService<BreakGlassAccounts>().IssueAsync(rotate, ct);
            var text = Format(credential);
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                await output.WriteAsync(text);
            }
            else
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using var writer = new StreamWriter(outputPath, options);
                await writer.WriteAsync(text);
                logger.LogInformation("Break-glass credential written to {Path}", outputPath);
            }

            return 0;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Break-glass command failed");
            return 1;
        }
    }

    public static string Format(BreakGlassCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return string.Join(Environment.NewLine,
        [
            "ARIVA BREAK-GLASS CREDENTIAL. Seal it and store it offline. Every sign-in raises a critical security event.",
            $"username: {credential.UserName}",
            $"password: {credential.Password}",
            "recovery codes (one per sign-in):",
            .. credential.RecoveryCodes.Select(code => "  " + code),
            string.Empty
        ]);
    }
}
