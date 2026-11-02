using System.Security.Cryptography;
using Ariva.Api.Common.Logging;
using Ariva.Core.Messaging;
using Ariva.Infra.Integration;
using Ariva.Infra.Messaging.Kafka;
using Ariva.Infra.Messaging.Outbox;
using Ariva.Infra.Security;
using FluentAssertions;

namespace Ariva.UnitTests.Messaging;

/// <summary>
/// ARV-285 (CWE-532, CWE-200): the exception text Ariva keeps outside the log pipeline is protected as log text is. A dead
/// letter's error goes to its dead-letter topic and, when Kafka does not take it, to an outbox row; the outbox's
/// <c>last_error</c> stays in the table for whoever reads it (wiki 10 section 4.5a). Each credential is drawn from Ariva's own
/// generator in the test; no row names one (ARV-252), and the checks are yes or no (ARV-249).
/// </summary>
public sealed class ProtectedErrorTextTests
{
    private const string Address = "anna.salem@example.com";

    private static bool Holds(string text, string part) => text.Contains(part, StringComparison.Ordinal);

    /// <summary>An integration client secret, a TOTP seed (as Ariva draws one) and an email address, in a library's refusal.</summary>
    private static (string Text, string Secret, string Seed) Refusal()
    {
        var secret = IntegrationCredentials.NewSecret();
        var seed = Base32.Encode(RandomNumberGenerator.GetBytes(20));
        return ($"endpoint refused client_secret {secret} with seed {seed} for {Address}", secret, seed);
    }

    private static void ShouldHoldNone(string text, string secret, string seed)
    {
        Holds(text, secret[IntegrationCredentials.SecretMarker.Length..]).Should().BeFalse("the client secret is masked");
        Holds(text, seed).Should().BeFalse("the TOTP seed is masked");
        Holds(text, "anna.salem@").Should().BeFalse("the address is masked");
        text.Should().Contain(IntegrationCredentials.SecretMarker + SecretText.Redacted).And.Contain("a***@example.com", "the text still says what was there");
    }

    [Fact]
    public void DeadLetter_Should_KeepTheErrorProtected_When_ItsMessageHoldsACredentialOrAnAddress()
    {
        var (text, secret, seed) = Refusal();

        var letter = DeadLetters.Create(KafkaTopics.FlowNowcast, 0, 7, "zone-1", [1, 2, 3], [], "ariva-it.probe", new InvalidOperationException(text), DateTime.UtcNow);

        ShouldHoldNone(letter.ErrorMessage, secret, seed);
        letter.ErrorType.Should().Be(typeof(InvalidOperationException).FullName);
        letter.Headers.Should().NotContainKey(DeadLetters.TextReplacedHeader, "protection is not a replacement of unprintable text");
    }

    [Fact]
    public void DeadLetter_Should_LeaveNoPartOfACredential_When_TheCutFallsInsideIt()
    {
        var secret = IntegrationCredentials.NewSecret();
        // The secret starts 12 characters before the 2,000 character cut: cut first, its marker and its first eight characters
        // would be kept, too few for its shape, so they would stay as they are.
        var text = new string('x', 2_000 - 13) + " " + secret;

        var letter = DeadLetters.Create(KafkaTopics.FlowNowcast, 0, 7, "zone-1", [], [], "ariva-it.probe", new InvalidOperationException(text), DateTime.UtcNow);

        Holds(letter.ErrorMessage, secret[4..12]).Should().BeFalse("the secret is masked whole before the cut");
        letter.ErrorMessage.Length.Should().BeLessThanOrEqualTo(2_000);
    }

    [Fact]
    public void LastError_Should_KeepTheFailureProtected_When_ItsMessageHoldsACredentialOrAnAddress()
    {
        var (text, secret, seed) = Refusal();

        var withType = OutboxRelay.LastError(new InvalidOperationException(text), withType: true);
        var bare = OutboxRelay.LastError(new InvalidOperationException(text), withType: false);

        ShouldHoldNone(withType, secret, seed);
        ShouldHoldNone(bare, secret, seed);
        withType.Should().StartWith("InvalidOperationException: endpoint refused");
        bare.Should().StartWith("endpoint refused");
    }

    [Fact]
    public void LastError_Should_StayWithinItsBoundAndPrintable_When_TheMessageIsLongOrHoldsANul()
    {
        var secret = IntegrationCredentials.NewSecret();
        // As in the dead letter: cut first, the marker and eight characters of the secret would be kept.
        var text = new string('y', OutboxRelay.MaxErrorLength - 13) + " " + secret;

        var kept = OutboxRelay.LastError(new InvalidOperationException(text), withType: false);

        kept.Length.Should().BeLessThanOrEqualTo(OutboxRelay.MaxErrorLength);
        Holds(kept, secret[4..12]).Should().BeFalse("protected before the cut");
        var printable = OutboxRelay.LastError(new InvalidOperationException("a\0b"), withType: false);
        Holds(printable, "\0").Should().BeFalse("PostgreSQL's text refuses a NUL, which would fail the row's update");
        printable.Should().Be("a\uFFFDb");
    }

    [Fact]
    public void LastError_Should_KeepTheTextAsItIs_When_ItHoldsNothingToProtect()
    {
        OutboxRelay.LastError(new TimeoutException("Local: Message timed out"), withType: true).Should().Be("TimeoutException: Local: Message timed out");
    }

    [Fact]
    public void Protect_Should_BeTheLogsProtection_When_TextIsKeptOutsideTheLogs()
    {
        var (text, _, _) = Refusal();

        SecretText.Protect(text).Should().Be(RedactionEnricher.ProtectText(text), "one set of rules for the logs and for the text kept elsewhere");
        SecretText.Redact(text).Should().Be(RedactionEnricher.RedactText(text));
    }

    [Fact]
    public void ProtectAndCut_Should_ProtectBeforeTheCut_When_TheTextIsLong()
    {
        SecretText.ProtectAndCut(null, 10).Should().BeEmpty();
        SecretText.ProtectAndCut("short", 10).Should().Be("short");
        var value = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        SecretText.ProtectAndCut("Password=" + value + " and more", 12).Should().Be("Password=[RE", "the value is masked, then the text is cut");
    }
}
