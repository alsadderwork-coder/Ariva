using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Security;
using Ariva.Api.Common.Settings;
using Ariva.Api.Main.Controllers.OpsArea.Displays;
using Ariva.Core.Advertising;
using Ariva.Core.Services.Displays;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-144, CWE-400 and CWE-306: the proof of play endpoint is display-authenticated, rate limited per display (6 batches a
/// minute by default) and capped at 64 KB a body, which holds the largest batch the rules take (100 plays) with room to
/// spare; the request model and the domain agree on the batch's bounds.
/// </summary>
public sealed class DisplayPlaysLimitTests
{
    [Fact]
    public void Record_Should_CarryTheDisplayCredentialTheBodyCapAndTheRateLimit_When_TheEndpointIsBuilt()
    {
        var record = typeof(DisplayPlaysController).GetMethod(nameof(DisplayPlaysController.Record))!;

        record.GetCustomAttribute<DisplayAuthenticatedAttribute>().Should().NotBeNull("only a display's own credential reports its plays");
        ((IRequestSizeLimitMetadata)record.GetCustomAttribute<RequestSizeLimitAttribute>()!).MaxRequestBodySize.Should().Be(DisplayPlaysController.MaxBodyBytes);
        typeof(DisplayPlaysController).GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName.Should().Be(RateLimitingExtensions.DisplayPlaysPolicy);
        record.GetCustomAttribute<HttpPostAttribute>()!.Template.Should().Be("plays");
    }

    [Fact]
    public void DisplayPlays_Should_AllowSixBatchesAMinute_When_NothingIsConfigured()
    {
        var limits = new RateLimitingSettings().DisplayPlays;

        limits.PermitLimit.Should().Be(6);
        limits.WindowSeconds.Should().Be(60);
        limits.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Request_Should_BoundTheBatchAsTheRulesDo_When_ItIsBound()
    {
        var plays = typeof(AdvertisingPlaysRequest).GetProperty(nameof(AdvertisingPlaysRequest.Plays))!;
        var parameter = typeof(AdvertisingPlaysRequest).GetConstructors().Single().GetParameters().Single(p => p.Name == nameof(AdvertisingPlaysRequest.Plays));
        var attributes = plays.GetCustomAttributes().Concat(parameter.GetCustomAttributes()).ToList();

        attributes.OfType<RequiredAttribute>().Should().NotBeEmpty();
        attributes.OfType<MinLengthAttribute>().Single().Length.Should().Be(1);
        attributes.OfType<MaxLengthAttribute>().Single().Length.Should().Be(AdvertisingPlays.MaxBatch);
    }

    [Fact]
    public void MaxBodyBytes_Should_HoldTheLargestBatch_When_EveryPlayIsAtItsLongest()
    {
        var play = new AdvertisingPlayRequest(Guid.Parse("0199a000-0000-7000-8000-0000000000c1"), new DateTime(2026, 10, 18, 23, 59, 59, 999, DateTimeKind.Utc).AddTicks(9_999),
            AdvertisingPlays.MaxDurationMilliseconds, false);
        var body = JsonSerializer.Serialize(new AdvertisingPlaysRequest(Enumerable.Repeat(play, AdvertisingPlays.MaxBatch).ToList()), JsonSerializerOptions.Web);

        Encoding.UTF8.GetByteCount(body).Should().BeLessThan(DisplayPlaysController.MaxBodyBytes / 2, "100 plays fit with room for indentation");
    }
}
