using System.Reflection;
using System.Text;
using Ariva.Api.Main.Controllers.AdminArea.Advertising;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Services.Displays;
using Ariva.Infra.Services.Displays;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-142b, CWE-400: the advertisement upload declares the ceiling every installation keeps (100 MB and framing), and
/// before its form is read the limit is lowered to what this installation takes (the larger of 10 MB and the video
/// setting, plus framing); a body declared larger answers 413 with the installation's size, never buffered. The form is
/// read in the filter, so a malformed or cut short one answers 400 with fixed text and the action never runs. (A body
/// sent without a length and cut off at the limit is shown end to end, tests/api/advertising-media.spec.ts.)
/// </summary>
public sealed class AdvertisingUploadLimitTests
{
    private const long Megabyte = 1024 * 1024;
    private const string Boundary = "arivaboundary";

    [Theory]
    [InlineData(20, 20 * Megabyte)]
    [InlineData(5, 10 * Megabyte)]
    [InlineData(100, 100 * Megabyte)]
    public async Task Filter_Should_LowerTheBodyLimitToWhatTheInstallationTakes_When_AnUploadStarts(int megabytes, long largest)
    {
        var (context, size) = Context(megabytes, Form("advertiser", "Gulf Air"));

        var ran = await RunAsync(context);

        ran.Should().BeTrue("a body within the limit is read and handed on");
        context.Result.Should().BeNull();
        size.MaxRequestBodySize.Should().Be(largest + AdvertisingUploadLimitAttribute.Framing);
        context.HttpContext.Request.Form["advertiser"].ToString().Should().Be("Gulf Air", "the form is read once, here");
    }

    [Fact]
    public async Task Filter_Should_Answer413WithTheInstallationsSize_When_TheBodyIsDeclaredLarger()
    {
        var (context, _) = Context(20, Form("advertiser", "Gulf Air"), contentLength: (20 * Megabyte) + AdvertisingUploadLimitAttribute.Framing + 1);

        (await RunAsync(context)).Should().BeFalse();

        var answer = context.Result.Should().BeOfType<ObjectResult>().Subject;
        answer.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        answer.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be("The file is larger than 20 MB.");
        AdvertisingMediaErrors.IsTooLarge(((ProblemDetails)answer.Value).Detail).Should().BeTrue();
    }

    [Fact]
    public async Task Filter_Should_Answer400WithFixedText_When_TheFormIsCutShort()
    {
        var cut = Form("advertiser", "<script>alert(1)</script>")[..60];
        var (context, _) = Context(20, cut);

        (await RunAsync(context)).Should().BeFalse("the action never sees a form it cannot read");

        var answer = context.Result.Should().BeOfType<ObjectResult>().Subject;
        answer.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        answer.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(AdvertisingUploadLimitAttribute.UnreadableForm, "never what was sent");
    }

    [Fact]
    public async Task Filter_Should_CutOffASlowBody_When_TheUploadIsOverHttp1()
    {
        var (context, _) = Context(20, Form("advertiser", "Gulf Air"));
        var rate = new BodyRate(refuses: false);
        context.HttpContext.Features.Set<IHttpMinRequestBodyDataRateFeature>(rate);
        context.HttpContext.Request.Protocol = "HTTP/1.1";

        (await RunAsync(context)).Should().BeTrue();

        rate.MinDataRate.Should().NotBeNull();
        rate.MinDataRate.BytesPerSecond.Should().Be(AdvertisingUploadLimitAttribute.MinBytesPerSecond);
        rate.MinDataRate.GracePeriod.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("HTTP/2")]
    [InlineData("HTTP/3")]
    public async Task Filter_Should_LeaveTheRateAlone_When_TheUploadIsOverHttp2Or3(string protocol)
    {
        var (context, _) = Context(20, Form("advertiser", "Gulf Air"));
        // As Kestrel's: any rate but none is refused on HTTP/2 and HTTP/3.
        context.HttpContext.Features.Set<IHttpMinRequestBodyDataRateFeature>(new BodyRate(refuses: true));
        context.HttpContext.Request.Protocol = protocol;

        (await RunAsync(context)).Should().BeTrue("the connection's own rate applies; nothing throws");
    }

    [Fact]
    public async Task Filter_Should_Answer408WithFixedText_When_TheBodyArrivesTooSlowly()
    {
        // Sent without a length, as a slow chunked sender does; the read is what Kestrel cuts off.
        var (context, _) = Context(20, [], contentLength: null);
        context.HttpContext.Request.Body = new SlowBody();

        (await RunAsync(context)).Should().BeFalse();

        var answer = context.Result.Should().BeOfType<ObjectResult>().Subject;
        answer.StatusCode.Should().Be(StatusCodes.Status408RequestTimeout);
        answer.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(AdvertisingUploadLimitAttribute.UnreadableForm);
    }

    [Fact]
    public async Task Filter_Should_NeverRaiseTheLimit_When_ALowerOneIsAlreadySet()
    {
        var (context, size) = Context(100, Form("advertiser", "Gulf Air"), contentLength: null);
        size.MaxRequestBodySize = Megabyte;

        (await RunAsync(context)).Should().BeTrue("without a declared length the read itself stops at the limit");

        size.MaxRequestBodySize.Should().Be(Megabyte);
    }

    [Fact]
    public void Upload_Should_DeclareTheCeilingAndLowerIt_When_TheEndpointIsBuilt()
    {
        var upload = typeof(AdvertisingMediaController).GetMethod(nameof(AdvertisingMediaController.Upload))!;

        upload.GetCustomAttribute<RequestSizeLimitAttribute>().Should().NotBeNull();
        upload.GetCustomAttribute<AdvertisingUploadLimitAttribute>().Should().NotBeNull("the installation's limit applies before the form is read");
        upload.GetCustomAttribute<RequestFormLimitsAttribute>()!.MultipartBodyLengthLimit
            .Should().Be(AdvertisingMedia.MaxVideoBytes + AdvertisingUploadLimitAttribute.Framing);
    }

    private static byte[] Form(string name, string value) =>
        Encoding.UTF8.GetBytes($"--{Boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n--{Boundary}--\r\n");

    private static async Task<bool> RunAsync(ResourceExecutingContext context)
    {
        var ran = false;
        await new AdvertisingUploadLimitAttribute().OnResourceExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult(new ResourceExecutedContext(context, []));
        });
        return ran;
    }

    private static (ResourceExecutingContext Context, BodySize Size) Context(int megabytes, byte[] body, long? contentLength = -1)
    {
        var services = new ServiceCollection()
            .AddSingleton(Options.Create(new AdvertisingVideoSettings { MaxMegabytes = megabytes }))
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.ContentType = $"multipart/form-data; boundary={Boundary}";
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentLength = contentLength == -1 ? body.Length : contentLength;
        var size = new BodySize { MaxRequestBodySize = AdvertisingMedia.MaxVideoBytes + AdvertisingUploadLimitAttribute.Framing };
        http.Features.Set<IHttpMaxRequestBodySizeFeature>(size);
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        return (new ResourceExecutingContext(action, [], []), size);
    }

    /// <summary>A request's minimum body rate; Kestrel's refuses any rate but none on HTTP/2 and HTTP/3.</summary>
    private sealed class BodyRate(bool refuses) : IHttpMinRequestBodyDataRateFeature
    {
        private Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate _rate;

        public Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate MinDataRate
        {
            get => _rate;
            set => _rate = refuses && value is not null ? throw new NotSupportedException("HTTP/2 and HTTP/3 take the connection's rate.") : value;
        }
    }

    /// <summary>A body that Kestrel cut off for arriving too slowly.</summary>
    private sealed class SlowBody : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw Slow();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw Slow();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw Slow();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static BadHttpRequestException Slow() => new("Reading the request body timed out due to data arriving too slowly.", StatusCodes.Status408RequestTimeout);
    }

    private sealed class BodySize : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }
}
