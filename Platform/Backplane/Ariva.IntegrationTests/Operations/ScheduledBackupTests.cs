using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ariva.Di;
using Ariva.UnitTests.Setup;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Ariva.IntegrationTests.Operations;

/// <summary>
/// ARV-182 (CWE-269, CWE-532, CWE-501): the chart's scheduled backup, its scripts run as the CronJob runs them, on a server
/// started as the TimescaleDB chart starts one (<see cref="ChartDatabase"/>) and migrated by the release's migration job. The
/// chart's files (Charts/platform/files/backup: the runner, the age encryptor and its copy of scripts/ops/ariva-db.sh) run in the
/// pinned image as its postgres user, reaching the server by a host name (<c>ariva-db.sh --host</c>) with SCRAM-SHA-256 only, with
/// the custodian's public recipient only. The set is the database encrypted to that recipient, read back by an independent
/// reading of the age format with the custodian's identity (which the scripts never see) into an archive pg_restore lists; its
/// manifest (takenBy the CronJob) names the file with its SHA-256, unsigned, and the log line gives both files' hashes. The
/// newest of the CronJob's own sets are kept, with a killed run's leftovers and the oldest set's signature removed, and an
/// operator's set, a directory and a set without the CronJob's manifest left alone; a failing run writes nothing and keeps every
/// set; a file another writer of the claim puts in place of the dump, or at its name, during the run fails it without a set and
/// without removing that writer's file (the review's findings 1 and 4); no output repeats the password or an identity; and the
/// commands that run as the superuser refuse another server.
/// </summary>
public sealed partial class ScheduledBackupTests : IAsyncLifetime
{
    #region Fields

    private const string RuntimeLogin = "ariva_app";
    private const string Namespace = "ariva-it";
    private const string Scripts = ChartDatabase.BackupDirectory + "/scripts";
    private const string Sets = ChartDatabase.BackupDirectory + "/sets";
    private const string Ready = ChartDatabase.BackupDirectory + "/watcher.ready";
    private const string TakenBy = "cronjob/ariva-db-backup";

    // A key pair as age-keygen makes one: the recipient Bech32-encoded under "age", the identity's scalar as hex and its
    // AGE-SECRET-KEY-1 form (to prove the runner refuses it). Written apart from the chart's encryptor, which only decodes.
    private const string KeygenCode = """
        from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey
        from cryptography.hazmat.primitives import serialization as s
        CHARSET = "qpzry9x8gf2tvdw0s3jn54khce6mua7l"
        def polymod(values):
            generator = [0x3B6A57B2, 0x26508E6D, 0x1EA119FA, 0x3D4233DD, 0x2A1462B3]
            check = 1
            for value in values:
                top = check >> 25
                check = ((check & 0x1FFFFFF) << 5) ^ value
                for i in range(5):
                    if (top >> i) & 1:
                        check ^= generator[i]
            return check
        def bech32(hrp, data):
            accumulator = bits = 0
            groups = []
            for byte in data:
                accumulator = (accumulator << 8) | byte
                bits += 8
                while bits >= 5:
                    bits -= 5
                    groups.append((accumulator >> bits) & 31)
            if bits:
                groups.append((accumulator << (5 - bits)) & 31)
            expanded = [ord(c) >> 5 for c in hrp] + [0] + [ord(c) & 31 for c in hrp]
            mod = polymod(expanded + groups + [0] * 6) ^ 1
            groups += [(mod >> 5 * (5 - i)) & 31 for i in range(6)]
            return hrp + "1" + "".join(CHARSET[g] for g in groups)
        key = X25519PrivateKey.generate()
        secret = key.private_bytes(s.Encoding.Raw, s.PrivateFormat.Raw, s.NoEncryption())
        public = key.public_key().public_bytes(s.Encoding.Raw, s.PublicFormat.Raw)
        print(bech32("age", public))
        print(secret.hex())
        print(bech32("age-secret-key-", secret).upper())
        """;

    // The age v1 format read back with the identity (argv: scalar hex, input, output), independently of the chart's encryptor:
    // one X25519 stanza, the header MAC, every 64 KiB chunk with its counter and the last one flagged. Exits non-zero on any
    // mismatch, so another identity or an altered byte opens nothing.
    private const string DecryptCode = """
        import base64, hashlib, hmac, sys
        from cryptography.hazmat.primitives import hashes, serialization as s
        from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey, X25519PublicKey
        from cryptography.hazmat.primitives.ciphers.aead import ChaCha20Poly1305
        from cryptography.hazmat.primitives.kdf.hkdf import HKDF
        def kdf(secret, salt, info):
            return HKDF(algorithm=hashes.SHA256(), length=32, salt=salt, info=info).derive(secret)
        def unb64(text):
            return base64.b64decode(text + b"=" * (-len(text) % 4), validate=True)
        data = open(sys.argv[2], "rb").read()
        key = X25519PrivateKey.from_private_bytes(bytes.fromhex(sys.argv[1]))
        mine = key.public_key().public_bytes(s.Encoding.Raw, s.PublicFormat.Raw)
        end = data.index(b"\n---") + 4
        lines = data[:end].split(b"\n")
        if lines[0] != b"age-encryption.org/v1" or len(lines) != 4 or not lines[1].startswith(b"-> X25519 "):
            sys.exit("not one X25519 stanza")
        share = unb64(lines[1].split(b" ")[2])
        file_key = ChaCha20Poly1305(kdf(key.exchange(X25519PublicKey.from_public_bytes(share)), share + mine, b"age-encryption.org/v1/X25519")).decrypt(bytes(12), unb64(lines[2]), None)
        mac_end = data.index(b"\n", end)
        if data[end:end + 1] != b" " or not hmac.compare_digest(unb64(data[end + 1:mac_end]), hmac.new(kdf(file_key, b"", b"header"), data[:end], hashlib.sha256).digest()):
            sys.exit("header MAC")
        nonce, payload = data[mac_end + 1:mac_end + 17], data[mac_end + 17:]
        stream = ChaCha20Poly1305(kdf(file_key, nonce, b"payload"))
        size = 64 * 1024 + 16
        count = max(1, -(-len(payload) // size))
        with open(sys.argv[3], "wb") as out:
            for i in range(count):
                out.write(stream.decrypt(i.to_bytes(11, "big") + (b"\x01" if i == count - 1 else b"\x00"), payload[i * size:(i + 1) * size], None))
        """;

    // Another writer of the claim (argv: the sets folder, a file to create once watching, the mode): as soon as a dump's .partial
    // appears it either puts a forged file in its place (swap) or creates a file at the dump's final name (occupy), then exits.
    private const string WatcherCode = """
        import os, sys, time
        directory, ready, mode = sys.argv[1], sys.argv[2], sys.argv[3]
        open(ready, "w").close()
        deadline = time.monotonic() + 120
        while time.monotonic() < deadline:
            for name in os.listdir(directory):
                if name.endswith(".dump.age.partial"):
                    target = os.path.join(directory, name)
                    if mode == "swap":
                        forged = os.path.join(directory, "forged.tmp")
                        with open(forged, "wb") as out:
                            out.write(b"age-encryption.org/v1\n-> X25519 forged\n")
                        os.replace(forged, target)
                    else:
                        with open(target[:-len(".partial")], "wb") as out:
                            out.write(b"another writer's file\n")
                    print(mode + " " + name)
                    sys.exit(0)
            time.sleep(0.005)
        sys.exit("no partial file appeared")
        """;

    private static readonly string RuntimePassword = "rt-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    private readonly string _workDirectory = Path.Combine(Path.GetTempPath(), "ariva-it-scheduled-backup-" + Guid.NewGuid().ToString("N"));
    private string _backupDirectory;
    private string _setsDirectory;
    private ChartDatabase _server;

    #endregion

    #region Properties

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #endregion

    #region Lifetime

    public async ValueTask InitializeAsync()
    {
        // The server's /backup: the chart's scripts, read-only for the database user, and the folder it writes the sets to.
        _backupDirectory = Path.Combine(_workDirectory, "backup");
        var scripts = Path.Combine(_backupDirectory, "scripts");
        _setsDirectory = Path.Combine(_backupDirectory, "sets");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(_setsDirectory);
        File.SetUnixFileMode(_backupDirectory, (UnixFileMode)0b111_111_111);
        File.SetUnixFileMode(scripts, (UnixFileMode)0b111_101_101);
        File.SetUnixFileMode(_setsDirectory, (UnixFileMode)0b111_111_111);
        foreach (var file in new[] { "ariva-backup.sh", "ariva-age.py", "ariva-db.sh" })
        {
            var target = Path.Combine(scripts, file);
            File.Copy(RepositoryPaths.Resolve("Platform/Cloud/Ariva.K8s/Helm/Charts/platform/files/backup/" + file), target);
            File.SetUnixFileMode(target, (UnixFileMode)0b110_100_100);
        }

        _server = await ChartDatabase.StartAsync(_backupDirectory, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
        if (Directory.Exists(_workDirectory))
            Directory.Delete(_workDirectory, recursive: true);
    }

    #endregion

    #region Tests

    [Fact(Timeout = 600_000)]
    public async Task ScheduledBackup_Should_WriteASetOnlyTheCustodianReads_When_TheCronJobRunsAgainstTheDatabaseService()
    {
        await MigrateAsync();
        var custodian = await CustodianAsync();

        // One run, as the CronJob's pod runs it: the database by a host name, the custodian's public recipient only.
        var run = await BackupAsync(custodian.Recipient, keep: 2);
        run.ExitCode.Should().Be(0, run.Stderr);
        var outcome = Outcome().Match(run.Stdout);
        outcome.Success.Should().BeTrue($"the last line names the set, its size and both hashes ({run.Stdout.Trim()})");
        var set = outcome.Groups["set"].Value;
        set.Should().StartWith($"ariva-scheduled-{Namespace}-", "the CronJob's sets are named apart from an operator's");
        outcome.Groups["kept"].Value.Should().Be("1");
        run.Stderr.Should().Contain("then sign " + set + ".manifest.json outside the cluster");
        Leaks(run, custodian).Should().BeFalse("no output carries the migration password or the identity (CWE-532)");

        // The set: the dump and its manifest, written for the database user alone, and nothing more.
        SetFiles().Should().BeEquivalentTo([set + ".dump.age", set + ".manifest.json"]);
        foreach (var file in SetFiles())
            File.GetUnixFileMode(Path.Combine(_setsDirectory, file)).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite, $"{file} is the database user's alone");
        (await _server.ExecAsync(Ct, "chmod", "0644", $"{Sets}/{set}.dump.age", $"{Sets}/{set}.manifest.json")).ExitCode.Should().Be(0, "the test reads what the database user wrote");

        var dump = await File.ReadAllBytesAsync(Path.Combine(_setsDirectory, set + ".dump.age"), Ct);
        System.Text.Encoding.ASCII.GetString(dump, 0, 32).Should().StartWith("age-encryption.org/v1\n-> X25519 ", "the age v1 format, to one X25519 recipient");
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(dump));
        outcome.Groups["sha256"].Value.Should().Be(sha256, "the logged hash is the dump's");
        outcome.Groups["bytes"].Value.Should().Be(dump.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(_setsDirectory, set + ".manifest.json"), Ct);
        outcome.Groups["manifest"].Value.Should().Be(Convert.ToHexStringLower(SHA256.HashData(manifestBytes)), "the logged manifest hash is the manifest's, which the operator checks before signing");

        // The manifest ariva-ops.mjs restore verifies once an operator has signed it: the set's namespace, the database, the file.
        using (var manifest = JsonDocument.Parse(manifestBytes))
        {
            var root = manifest.RootElement;
            root.GetProperty("format").GetString().Should().Be("ariva-backup-set/1");
            root.GetProperty("takenBy").GetString().Should().Be(TakenBy);
            root.GetProperty("namespace").GetString().Should().Be(Namespace);
            root.GetProperty("database").GetProperty("format").GetString().Should().Be("ariva-backup-manifest/1");
            root.GetProperty("database").GetProperty("database").GetString().Should().Be(ChartDatabase.Database);
            root.GetProperty("database").GetProperty("latestScript").GetString().Should().MatchRegex("^[0-9]{4}_[a-z0-9_]+\\.sql$");
            var file = root.GetProperty("files").EnumerateArray().ToList().Should().ContainSingle().Subject;
            file.GetProperty("label").GetString().Should().Be("database");
            file.GetProperty("file").GetString().Should().Be(set + ".dump.age");
            file.GetProperty("bytes").GetInt64().Should().Be(dump.Length);
            file.GetProperty("sha256").GetString().Should().Be(sha256);
        }

        // Only the identity reads it: an independent reading of the format (header MAC, every chunk, the last flag) gives the
        // archive back, and pg_restore lists Ariva's tables in it.
        var opened = await _server.ExecAsync(Ct, "python3", "-I", "-c", DecryptCode, custodian.Identity, $"{Sets}/{set}.dump.age", "/tmp/restored.dump");
        opened.ExitCode.Should().Be(0, opened.Stderr);
        var listed = await _server.ExecAsync(Ct, "pg_restore", "--list", "/tmp/restored.dump");
        listed.ExitCode.Should().Be(0, listed.Stderr);
        listed.Stdout.Should().Contain("TABLE public schema_version").And.Contain("TABLE public data_protection_key").And.Contain("TABLE DATA public schema_version");
        (await _server.ExecAsync(Ct, "rm", "-f", "/tmp/restored.dump")).ExitCode.Should().Be(0);
        var other = await CustodianAsync();
        (await _server.ExecAsync(Ct, "python3", "-I", "-c", DecryptCode, other.Identity, $"{Sets}/{set}.dump.age", "/tmp/other.dump")).ExitCode
            .Should().NotBe(0, "another identity opens nothing");
    }

    [Fact(Timeout = 600_000)]
    public async Task ScheduledBackup_Should_KeepItsOwnNewestSetsAndLeaveNothingOfAFailedRun_When_RunAgain()
    {
        await MigrateAsync();
        var custodian = await CustodianAsync();
        var first = await BackupAsync(custodian.Recipient, keep: 2);
        first.ExitCode.Should().Be(0, first.Stderr);
        var names = new List<string> { Outcome().Match(first.Stdout).Groups["set"].Value };

        // A killed run's leftover and an operator's signature beside the oldest set go with the pruning; what is not one of the
        // CronJob's sets stays (the review's finding 5): an operator's set of the same namespace (ariva-ops.mjs names it
        // ariva-<namespace>-<time>), a directory with a set's name, a set of the CronJob's name whose manifest is not the
        // CronJob's, and any other file.
        var leftover = Path.Combine(_setsDirectory, $"ariva-scheduled-{Namespace}-20260101T000000Z.dump.age.partial");
        var signature = Path.Combine(_setsDirectory, names[0] + ".manifest.json.sig");
        var operatorSet = $"ariva-{Namespace}-20250101T000000Z";
        var foreignSet = $"ariva-scheduled-{Namespace}-20250102T000000Z";
        var directory = Path.Combine(_setsDirectory, $"ariva-scheduled-{Namespace}-20250103T000000Z.dump.age");
        var planted = new Dictionary<string, string>
        {
            [leftover] = "x",
            [signature] = "x",
            [Path.Combine(_setsDirectory, "notes.txt")] = "x",
            [Path.Combine(_setsDirectory, operatorSet + ".dump.age")] = "x",
            [Path.Combine(_setsDirectory, operatorSet + ".files.tar.age")] = "x",
            [Path.Combine(_setsDirectory, operatorSet + ".manifest.json")] = $$"""{"format":"ariva-backup-set/1","namespace":"{{Namespace}}","files":[{"label":"database","file":"{{operatorSet}}.dump.age"}]}""",
            [Path.Combine(_setsDirectory, foreignSet + ".dump.age")] = "x",
            [Path.Combine(_setsDirectory, foreignSet + ".manifest.json")] = $$"""{"format":"ariva-backup-set/1","namespace":"{{Namespace}}","files":[{"file":"{{foreignSet}}.dump.age"}]}"""
        };
        foreach (var (path, text) in planted)
        {
            await File.WriteAllTextAsync(path, text, Ct);
            File.SetUnixFileMode(path, (UnixFileMode)0b110_110_110);
        }

        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, (UnixFileMode)0b111_111_111);
        var stays = new[] { "notes.txt", operatorSet + ".dump.age", operatorSet + ".files.tar.age", operatorSet + ".manifest.json", foreignSet + ".dump.age", foreignSet + ".manifest.json" };

        var runs = new List<ExecResult>();
        for (var i = 0; i < 2; i++)
        {
            await Task.Delay(1_100, Ct);
            var again = await BackupAsync(custodian.Recipient, keep: 2);
            again.ExitCode.Should().Be(0, again.Stderr);
            Outcome().Match(again.Stdout).Groups["kept"].Value.Should().Be("2", "the CronJob's own sets only are counted and kept");
            names.Add(Outcome().Match(again.Stdout).Groups["set"].Value);
            runs.Add(again);
        }

        SetFiles().Should().BeEquivalentTo(names.Skip(1).SelectMany(name => new[] { name + ".dump.age", name + ".manifest.json" }).Concat(stays),
            "the newest two of the CronJob's sets stay; the oldest goes with its signature and the killed run's leftover; nothing else is touched");
        Directory.Exists(directory).Should().BeTrue("a directory is no set");
        runs[1].Stderr.Should().Contain($"removed the set {names[0]} (keeping the newest 2)").And.Contain($"left alone: {foreignSet} (no manifest of this CronJob beside it)");
        string.Concat(runs.Select(r => r.Stderr)).Should().NotContain("removed the set " + operatorSet).And.NotContain($"removed the set ariva-scheduled-{Namespace}-20250103T000000Z");

        // A failing run (the migration login refused) writes nothing, keeps every set and says where it stopped.
        await Task.Delay(1_100, Ct);
        var before = SetFiles();
        var failing = await BackupAsync(custodian.Recipient, keep: 2, "ARIVA_MIGRATION_PASSWORD=not-the-password-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));
        failing.ExitCode.Should().Be(1, failing.Stderr);
        failing.Stdout.Trim().Should().Be("ariva-backup: outcome=failed step=manifest");
        SetFiles().Should().BeEquivalentTo(before, "a failed run leaves no file and removes no set");
        Leaks(failing, custodian).Should().BeFalse();

        // A pod's dump that fails part way (a table the migration login may not read) leaves no partial file either.
        (await _server.ExecAsync(Ct, "psql", "--no-psqlrc", "--quiet", "--set=ON_ERROR_STOP=1", "--host=/var/run/postgresql", "--username=postgres",
            "--dbname=" + ChartDatabase.Database, "--command=CREATE TABLE it_unreadable AS SELECT 1 AS x")).ExitCode.Should().Be(0, "the superuser's table, which the migration login may not read");
        await Task.Delay(1_100, Ct);
        var partWay = await BackupAsync(custodian.Recipient, keep: 2);
        partWay.ExitCode.Should().Be(1, partWay.Stderr);
        partWay.Stdout.Trim().Should().Be("ariva-backup: outcome=failed step=backup");
        partWay.Stderr.Should().Contain("permission denied for table it_unreadable");
        SetFiles().Should().BeEquivalentTo(before);
    }

    [Fact(Timeout = 600_000)]
    public async Task ScheduledBackup_Should_FailWithoutASet_When_AnotherWriterReplacesOrTakesTheDumpDuringTheRun()
    {
        await MigrateAsync();
        var custodian = await CustodianAsync();

        // A forged file put in place of the dump's .partial while the dump streams (the review's finding 1): the run holds the
        // published file to the hash of what it wrote, so it fails, logs no success and removes the forged file from the set's name.
        var swapped = await WithAnotherWriterAsync("swap", () => BackupAsync(custodian.Recipient, keep: 2));
        swapped.ExitCode.Should().Be(1, swapped.Stderr);
        swapped.Stdout.Trim().Should().Be("ariva-backup: outcome=failed step=backup", "a replaced dump is never reported as a backup");
        swapped.Stderr.Should().Contain("on the claim is not what this run wrote").And.Contain("another writer replaced it");
        SetFiles().Should().BeEmpty("no set, no forged file under a set's name, no .partial");

        // A file another writer creates at the dump's final name while the dump streams (the review's finding 4): the run's own
        // file never replaces it, the run fails, and the other writer's file is not removed.
        await Task.Delay(1_100, Ct);
        var occupied = await WithAnotherWriterAsync("occupy", () => BackupAsync(custodian.Recipient, keep: 2));
        occupied.ExitCode.Should().Be(1, occupied.Stderr);
        occupied.Stdout.Trim().Should().Be("ariva-backup: outcome=failed step=backup");
        occupied.Stderr.Should().Contain("already exists (another writer of the claim)");
        var others = SetFiles();
        others.Should().ContainSingle().Which.Should().MatchRegex($"^ariva-scheduled-{Namespace}-[0-9]{{8}}T[0-9]{{6}}Z\\.dump\\.age$", "only the other writer's file is left: the run's .partial went");
        (await File.ReadAllTextAsync(Path.Combine(_setsDirectory, others[0]), Ct)).Should().Be("another writer's file\n", "the run did not remove or replace it");
    }

    [Fact(Timeout = 300_000)]
    public async Task ScheduledBackup_Should_RefuseBeforeConnecting_When_ItsInputIsAnIdentityOrMalformed()
    {
        var custodian = await CustodianAsync();

        // The identity given as the recipient: refused without being repeated, and nothing written.
        var identity = await BackupAsync(custodian.IdentityText, keep: 2);
        identity.ExitCode.Should().Be(2);
        identity.Stderr.Should().Contain("ARIVA_BACKUP_RECIPIENT holds an age identity (a private key)");
        identity.Stdout.Trim().Should().Be("ariva-backup: outcome=failed step=input");
        Leaks(identity, custodian).Should().BeFalse("the refusal never repeats the identity");

        foreach (var (setting, message) in new[]
                 {
                     ("ARIVA_BACKUP_KEEP=0", "ARIVA_BACKUP_KEEP must be a whole number from 1 to 90"),
                     ("ARIVA_BACKUP_KEEP=91", "ARIVA_BACKUP_KEEP must be a whole number from 1 to 90"),
                     ("ARIVA_BACKUP_NAMESPACE=Ariva_IT", "ARIVA_BACKUP_NAMESPACE must be a Kubernetes namespace name"),
                     ("ARIVA_DATABASE_HOST=postgres://localhost", "ARIVA_DATABASE_HOST must be a lower-case host name or IPv4 address"),
                     ("ARIVA_BACKUP_DIRECTORY=/backup/../etc", "ARIVA_BACKUP_DIRECTORY must be a plain absolute path"),
                     ("ARIVA_MIGRATION_PASSWORD=", "ARIVA_MIGRATION_PASSWORD is not set")
                 })
        {
            var refused = await BackupAsync(custodian.Recipient, keep: 2, setting);
            refused.ExitCode.Should().Be(2, setting);
            refused.Stderr.Should().Contain(message, setting);
        }

        // A recipient whose checksum is wrong (one character changed) is refused by the encryptor's check, before anything connects.
        var broken = custodian.Recipient[..^1] + (custodian.Recipient[^1] == 'q' ? 'p' : 'q');
        var checksum = await BackupAsync(broken, keep: 2);
        checksum.ExitCode.Should().Be(2, checksum.Stderr);
        checksum.Stderr.Should().Contain("--recipient has a wrong checksum").And.Contain("ARIVA_BACKUP_RECIPIENT is not a usable age recipient");
        checksum.Stdout.Trim().Should().Be("ariva-backup: outcome=failed step=input");
        SetFiles().Should().BeEmpty("no run wrote a file");

        // The commands that run as the superuser over the pod's socket refuse another server than the pod's own (ARV-182).
        foreach (var command in new[] { "check", "restore", "update-extensions", "grant-settings" })
        {
            var remote = await _server.OpsAsync(Ct, command, "--host", "localhost");
            remote.ExitCode.Should().Be(1, command);
            remote.Stderr.Should().Contain($"--host applies to backup and manifest only: {command} runs in the database pod against its own server");
        }
    }

    #endregion

    #region Helpers

    private async Task MigrateAsync() =>
        (await DatabaseMigration.RunAsync(new ConfigurationBuilder().AddInMemoryCollection(_server.MigrationSettings(RuntimeLogin, RuntimePassword)).Build(), Ct))
            .Should().Be(0, "the release's migration job made the database an Ariva one");

    /// <summary>The runner as the CronJob starts it (dev: no TLS, SCRAM-SHA-256 only), the server's own database, login and password beneath it.</summary>
    private Task<ExecResult> BackupAsync(string recipient, int keep, params string[] overrides) => _server.ExecAsync(Ct,
    [
        "env", "ARIVA_BACKUP_RECIPIENT=" + recipient, $"ARIVA_BACKUP_KEEP={keep}", "ARIVA_BACKUP_NAMESPACE=" + Namespace, "ARIVA_BACKUP_DIRECTORY=" + Sets,
        "ARIVA_BACKUP_SCRIPTS=" + Scripts, "ARIVA_DATABASE_HOST=localhost", "ARIVA_DATABASE_PORT=5432", "PGSSLMODE=prefer", "PGREQUIREAUTH=scram-sha-256", .. overrides,
        "bash", Scripts + "/ariva-backup.sh"
    ]);

    /// <summary>Runs the backup while another writer of the claim (WatcherCode) waits for its .partial, and returns the backup's result.</summary>
    private async Task<ExecResult> WithAnotherWriterAsync(string mode, Func<Task<ExecResult>> backup)
    {
        var ready = Path.Combine(_backupDirectory, "watcher.ready");
        File.Delete(ready);
        var watcher = _server.ExecAsync(Ct, "python3", "-I", "-c", WatcherCode, Sets, Ready, mode);
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!File.Exists(ready))
        {
            DateTime.UtcNow.Should().BeBefore(until, "the other writer starts watching");
            await Task.Delay(50, Ct);
        }

        var result = await backup();
        var watched = await watcher;
        watched.ExitCode.Should().Be(0, $"the other writer acted during the run ({watched.Stderr.Trim()})");
        watched.Stdout.Should().StartWith(mode + " ");
        return result;
    }

    private string[] SetFiles() => [.. Directory.EnumerateFiles(_setsDirectory).Select(Path.GetFileName).Where(name => name != ".ariva-backup.lock").Order(StringComparer.Ordinal)];

    private bool Leaks(ExecResult result, Custodian custodian)
    {
        var output = result.Stdout + result.Stderr;
        return output.Contains(_server.MigrationPassword, StringComparison.Ordinal) || output.Contains(custodian.Identity, StringComparison.OrdinalIgnoreCase)
            || output.Contains("AGE-SECRET-KEY-1", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The custodian's key pair, made in the server's image (pyca/cryptography): the recipient, and the identity as hex and as age writes it.</summary>
    private async Task<Custodian> CustodianAsync()
    {
        var made = await _server.ExecAsync(Ct, "python3", "-I", "-c", KeygenCode);
        made.ExitCode.Should().Be(0, made.Stderr);
        var lines = made.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Should().HaveCount(3);
        lines[0].Should().MatchRegex("^age1[qpzry9x8gf2tvdw0s3jn54khce6mua7l]{58}$");
        return new Custodian(lines[0], lines[1], lines[2]);
    }

    [GeneratedRegex(@"^ariva-backup: outcome=succeeded set=(?<set>ariva-scheduled-[a-z0-9-]+-[0-9]{8}T[0-9]{6}Z) bytes=(?<bytes>[0-9]+) sha256=(?<sha256>[0-9a-f]{64}) manifest_sha256=(?<manifest>[0-9a-f]{64}) kept=(?<kept>[0-9]+)$", RegexOptions.Multiline)]
    private static partial Regex Outcome();

    private sealed record Custodian(string Recipient, string Identity, string IdentityText);

    #endregion
}
