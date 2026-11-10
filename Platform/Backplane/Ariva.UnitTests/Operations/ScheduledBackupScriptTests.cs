using System.Text.RegularExpressions;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Operations;

/// <summary>
/// ARV-182 (CWE-78, CWE-269, CWE-501, CWE-532): the chart's scheduled backup runs the operators' own tool and keeps its secrets
/// where they belong. The chart carries scripts/ops/ariva-db.sh byte for byte; that script reaches another server than its pod's
/// only for backup and manifest, the two commands that never run as the superuser; the runner gives the migration password to
/// ariva-db.sh alone, builds no command from a value, holds each published file to the hash of what it wrote and prunes its own
/// sets alone; the encryptor uses the image's pyca/cryptography and the system's random source only, matches its inputs whole and
/// starts nothing. The scripts' behaviour is proven by Ariva.IntegrationTests ScheduledBackupTests and their
/// chart render by Platform/Cloud/Ariva.K8s/tests/chart-security.mjs.
/// </summary>
public sealed partial class ScheduledBackupScriptTests
{
    #region Fields

    private const string Chart = "Platform/Cloud/Ariva.K8s/Helm/Charts/platform/files/backup/";

    private static readonly Lazy<string> Operators = new(() => File.ReadAllText(RepositoryPaths.Resolve("scripts/ops/ariva-db.sh")));
    private static readonly Lazy<string> Runner = new(() => File.ReadAllText(RepositoryPaths.Resolve(Chart + "ariva-backup.sh")));
    private static readonly Lazy<string> Encryptor = new(() => File.ReadAllText(RepositoryPaths.Resolve(Chart + "ariva-age.py")));

    #endregion

    #region Tests

    [Fact]
    public void ChartCopy_Should_BeTheOperatorsScript_When_TheScheduledBackupRunsIt() =>
        string.Equals(File.ReadAllText(RepositoryPaths.Resolve(Chart + "ariva-db.sh")), Operators.Value, StringComparison.Ordinal)
            .Should().BeTrue("the CronJob runs the operators' ariva-db.sh: copy scripts/ops/ariva-db.sh over the chart's files/backup/ariva-db.sh");

    [Fact]
    public void Host_Should_ReachAnotherServerForBackupAndManifestOnly_When_TheScriptIsRead()
    {
        var text = Operators.Value;

        // Every other command refuses --host before anything runs: its superuser steps go to this pod's socket.
        text.Should().Contain("if [[ \"$HOST\" != \"127.0.0.1\" && \"$command\" != \"backup\" && \"$command\" != \"manifest\" ]]; then");
        text.Should().Contain("HOST=127.0.0.1\n");
        HostOption().Matches(text).Select(m => m.Groups["value"].Value).Should().BeEquivalentTo(["\"$HOST\"", "\"$SOCKET_DIR\"", "\"$HOST\"", "\"$SOCKET_DIR\""],
            "the migration login's psql and pg_dump take --host, the superuser's psql and pg_restore the socket only");
        SuperuserHelper().Match(text).Value.Should().Contain("--host=\"$SOCKET_DIR\"").And.NotContain("$HOST");
    }

    [Fact]
    public void Runner_Should_GiveThePasswordToArivaDbShAlone_When_Read()
    {
        var text = Runner.Value;

        text.Should().StartWith("#!/usr/bin/env bash");
        text.Should().Contain("set -euo pipefail").And.Contain("umask 077");
        ForbiddenShell().Matches(text).Select(m => m.Value).Should().BeEmpty("no eval, no shell started with a string, no backticks");
        var uses = text.Split('\n').Where(line => line.Contains("PASSWORD", StringComparison.Ordinal)).Select(line => line.Trim()).ToList();
        uses.Should().NotBeEmpty();
        uses.Should().OnlyContain(line =>
                line.StartsWith("PASSWORD=\"${ARIVA_MIGRATION_PASSWORD:-}\"", StringComparison.Ordinal)
                || line.StartsWith("export -n ARIVA_MIGRATION_PASSWORD", StringComparison.Ordinal)
                || line.StartsWith("unset ARIVA_MIGRATION_PASSWORD PGPASSWORD POSTGRES_PASSWORD", StringComparison.Ordinal)
                || line.StartsWith("[[ -n \"$PASSWORD\" ]] || refuse ", StringComparison.Ordinal)
                || line.StartsWith("ARIVA_MIGRATION_PASSWORD=\"$PASSWORD\" bash \"$SCRIPTS/ariva-db.sh\" ", StringComparison.Ordinal)
                || line.StartsWith("# ", StringComparison.Ordinal),
            "the password reaches ariva-db.sh alone, in its environment, and no other program or command line");
        text.Should().Contain("python3 -I \"$SCRIPTS/ariva-age.py\" --recipient \"$RECIPIENT\"", "the encryptor runs isolated, with the public recipient only");
        text.Should().NotContain("set -x").And.NotContain("printenv").And.NotContain("declare -p");
    }

    [Fact]
    public void Runner_Should_HoldEachPublishedFileToTheHashOfWhatItWrote_When_Read()
    {
        var text = Runner.Value;

        // CWE-501 (the ARV-182 review): the hashes come from the stream and the pod's private copy, never from the claim, the
        // published files must match them, no published name is replaced, and the retention reads the CronJob's own sets alone.
        text.Should().Contain("set -o noclobber");
        text.Should().Contain("--recipient \"$RECIPIENT\" --digest \"$WORK/dump.digest\" > \"$dump.partial\"");
        text.Should().Contain("same_as_written \"$dump\" \"$sha256\" \"$bytes\"")
            .And.Contain("same_as_written \"$manifest\" \"$manifest_sha256\" \"$manifest_bytes\"");
        text.Should().Contain("mv -n -T -- \"$1\" \"$2\"", "a published name is never replaced");
        text.Should().Contain("readonly TAKEN_BY=\"cronjob/ariva-db-backup\"").And.Contain(".takenBy == $taken");
        ClaimGlobs().Matches(text).Select(m => m.Value).Should().HaveCount(2).And.OnlyContain(
            glob => glob.StartsWith("\"$DIRECTORY\"/ariva-scheduled-\"$NAMESPACE\"-", StringComparison.Ordinal),
            "the retention lists the CronJob's own naming, never an operator's ariva-<namespace>-<time> sets");
    }

    [Fact]
    public void Encryptor_Should_MatchItsInputsWholeAndWriteItsDigestToANewFile_When_Read()
    {
        var text = Encryptor.Value;

        text.Should().Contain("RECIPIENT.fullmatch(text)").And.Contain("DIGEST_PATH.fullmatch(digest)")
            .And.NotContain(".match(", "a pattern matched with match and $ would also take a trailing line break");
        text.Should().Contain("os.open(digest, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)", "the digest goes to a new file, never one put there");
    }

    [Fact]
    public void Encryptor_Should_UseTheImagesPrimitivesAndTheSystemsRandomSource_When_Read()
    {
        var text = Encryptor.Value;

        Imports().Matches(text).Select(m => m.Groups["module"].Value).Distinct().Should().BeEquivalentTo(
            ["base64", "hashlib", "hmac", "os", "re", "sys", "cryptography.hazmat.primitives", "cryptography.hazmat.primitives.asymmetric.x25519",
             "cryptography.hazmat.primitives.ciphers.aead", "cryptography.hazmat.primitives.kdf.hkdf"],
            "nothing but the standard library's encodings and pyca/cryptography's X25519, ChaCha20-Poly1305 and HKDF");
        text.Should().Contain("file_key = os.urandom(16)").And.Contain("nonce = os.urandom(16)").And.Contain("ephemeral = X25519PrivateKey.generate()");
        var code = string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));
        ForbiddenPython().Matches(code).Select(m => m.Value).Should().BeEmpty("no other random source, no program started, no code evaluated");
        text.Should().Contain("b\"age-encryption.org/v1/X25519\"").And.Contain("b\"header\"").And.Contain("b\"payload\"").And.Contain("CHUNK = 64 * 1024");
    }

    #endregion

    #region Patterns

    [GeneratedRegex(@"(^|[\s;|&(])(eval|(ba)?sh\s+-c)\s|`", RegexOptions.Multiline)]
    private static partial Regex ForbiddenShell();

    [GeneratedRegex(@"--host=(?<value>""\$[A-Z_]+"")")]
    private static partial Regex HostOption();

    [GeneratedRegex(@"as_superuser\(\) \{.*?\n\}", RegexOptions.Singleline)]
    private static partial Regex SuperuserHelper();

    [GeneratedRegex(@"""\$DIRECTORY""/[^\s;]+")]
    private static partial Regex ClaimGlobs();

    [GeneratedRegex(@"^(?:import (?<module>[a-z0-9_.]+)|from (?<module>[a-z0-9_.]+) import )", RegexOptions.Multiline)]
    private static partial Regex Imports();

    [GeneratedRegex(@"\b(random|secrets\.choice|subprocess|os\.system|os\.popen|os\.exec\w*|eval|exec|__import__|pickle)\b")]
    private static partial Regex ForbiddenPython();

    #endregion
}
