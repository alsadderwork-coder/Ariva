using Ariva.Core.Domain.Common;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Security;
using Ariva.Core.Services;
using Ariva.Core.Services.Sensing;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Sensing;
using Ariva.Infra.Services.Topology;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.UnitTests.Services;

/// <summary>
/// ARV-281 (CWE-209): the topology and device services answer a domain rule's own text as a 400 and nothing else. An
/// exception thrown outside the domain model (the framework's, the driver's, a mock storage's here) passes through their
/// guards, so the global handler answers it as a 500 without its text outside vm-local; before, any ArgumentException's
/// message and any InvalidOperationException naming "already exists" reached the answer. A flush that fails for any reason
/// leaves the unit of work promised not to commit, so its end does not send the aborted transaction again. ARV-282: a rule
/// refusal is matched by its type (<see cref="IDomainRule"/>) and kind alone, so it is answered wherever it is thrown, and an
/// exception that is not a domain rule passes through whatever its text.
/// </summary>
public sealed class TopologyGuardTests
{
    #region Fields

    private const string Detail = "framework detail 7f3c";

    #endregion

    #region Methods

    private static Mock<IUnitOfWork> FailingStorage(Exception error)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.StorageProvider).Throws(error);
        return unitOfWork;
    }

    private static Mock<ISiteScope> AllSites()
    {
        var scope = new Mock<ISiteScope>();
        scope.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SiteAccess(true, new HashSet<string>()));
        return scope;
    }

    private static FusionCache Cache() => new(new FusionCacheOptions(), new MemoryCache(new MemoryCacheOptions()));

    private static SvcTopology Topology(Mock<IUnitOfWork> unitOfWork)
    {
        var user = new Mock<ICurrentUser>().Object;
        return new SvcTopology(unitOfWork.Object, user, TimeProvider.System, AllSites().Object, new AuditTrail(unitOfWork.Object, user, TimeProvider.System), Cache());
    }

    private static SvcZoneProfiles ZoneProfiles(Mock<IUnitOfWork> unitOfWork)
    {
        var user = new Mock<ICurrentUser>().Object;
        return new SvcZoneProfiles(unitOfWork.Object, user, TimeProvider.System, AllSites().Object, new AuditTrail(unitOfWork.Object, user, TimeProvider.System));
    }

    /// <summary>A domain rule of the kind named, raised by the storage: far from any entity's frame.</summary>
    private static Exception Rule(string kind) => kind switch
    {
        "rule" => new DomainRuleException("The rule's text."),
        "argument" => new DomainArgumentException("The argument rule's text.", "name"),
        "range" => new DomainArgumentOutOfRangeException("floorNumber", 99, "The range rule's text."),
        "code" => new DomainRuleException("Terminal T1 already exists here.", DomainRuleKind.CodeTaken),
        _ => new DomainRuleException("A floor text of any wording.", DomainRuleKind.FloorTaken)
    };

    private static string Answer(string kind) => kind switch
    {
        "rule" => "The rule's text.",
        "argument" => "The argument rule's text.",
        "range" => "The range rule's text.",
        "code" => TopologyErrors.Duplicate,
        _ => TopologyErrors.FloorTaken
    };

    private static SvcDevices Devices(Mock<IUnitOfWork> unitOfWork)
    {
        var user = new Mock<ICurrentUser>().Object;
        return new SvcDevices(unitOfWork.Object, user, TimeProvider.System, AllSites().Object, new AuditTrail(unitOfWork.Object, user, TimeProvider.System), Cache(),
            new Mock<IDeviceMappingCatalog>().Object);
    }

    private static Exception Caught(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            return e;
        }

        throw new InvalidOperationException("the action threw nothing");
    }

    [Fact]
    public async Task CreateTerminal_Should_LetTheExceptionThrough_When_AFrameworkArgumentExceptionIsRaised()
    {
        var create = () => Topology(FailingStorage(new ArgumentException(Detail))).CreateTerminalAsync(new CreateTerminalRequest(Guid.NewGuid(), "T1", "T1", "ABC"), CancellationToken.None);

        (await create.Should().ThrowAsync<ArgumentException>()).WithMessage(Detail);
    }

    [Theory]
    [InlineData("framework detail 7f3c")]
    [InlineData("Terminal T1 already exists here.")]
    public async Task CreateTerminal_Should_LetTheExceptionThrough_When_AnInvalidOperationExceptionIsNotTheDomains(string message)
    {
        var create = () => Topology(FailingStorage(new InvalidOperationException(message))).CreateTerminalAsync(new CreateTerminalRequest(Guid.NewGuid(), "T1", "T1", "ABC"), CancellationToken.None);

        (await create.Should().ThrowAsync<InvalidOperationException>()).WithMessage(message, "only the domain's own duplicate is read as Duplicate");
    }

    [Fact]
    public async Task Register_Should_LetTheExceptionThrough_When_AFrameworkArgumentExceptionIsRaised()
    {
        var request = new RegisterDeviceRequest("S-1", "StereoVision", "PC2SE", "HttpsPush", "Xovis", "Ntp", new DevicePlacement(Guid.NewGuid(), 20, 16, 5, 0, "Snake A"));
        var register = () => Devices(FailingStorage(new ArgumentException(Detail))).RegisterAsync(request, CancellationToken.None);

        (await register.Should().ThrowAsync<ArgumentException>()).WithMessage(Detail);
    }

    [Theory]
    [InlineData("driver")]
    [InlineData("cancelled")]
    public async Task UpdateTerminal_Should_PromiseNotToCommitAndLetTheErrorThrough_When_TheFlushFails(string kind)
    {
        Exception error = kind == "driver"
            ? new global::NHibernate.Exceptions.GenericADOException("could not execute batch command", new InvalidOperationException(Detail))
            : new OperationCanceledException();
        var airport = new Airport("TQR", null, "Race", "Asia/Amman") { Id = Guid.NewGuid() };
        var terminal = airport.AddTerminal("T1", "Terminal 1", "TQR");
        terminal.Id = Guid.NewGuid();
        var storage = new Mock<IStorageProvider>();
        storage.Setup(s => s.GetAsync<Terminal>(terminal.Id.Value, It.IsAny<CancellationToken>())).ReturnsAsync(terminal);
        storage.Setup(s => s.FlushAsync(It.IsAny<CancellationToken>())).ThrowsAsync(error);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.StorageProvider).Returns(storage.Object);
        var promises = new List<bool>();
        unitOfWork.Setup(u => u.PromiseToCommit()).Callback(() => promises.Add(true));
        unitOfWork.Setup(u => u.PromiseNotToCommit()).Callback(() => promises.Add(false));

        var update = () => Topology(unitOfWork).UpdateTerminalAsync(terminal.Id.Value, new UpdateTerminalRequest("Renamed"), CancellationToken.None);

        (await update.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(error, "neither a driver error nor a cancellation is answered as a rule");
        storage.Verify(s => s.FlushAsync(It.IsAny<CancellationToken>()), Times.Once());
        promises.Should().Equal([true, true, false], "the update and its audit entry promised a commit, and the failed flush took it back");
    }

    [Theory]
    [InlineData("rule")]
    [InlineData("argument")]
    [InlineData("range")]
    [InlineData("code")]
    [InlineData("floor")]
    public async Task CreateTerminal_Should_AnswerTheRuleByItsKind_When_ADomainRuleIsRaisedAnywhere(string kind)
    {
        // ARV-282: matched by type and kind, not by the frame that threw it (a Release build can inline an entity's rule into
        // the service) or by its text: a code or floor taken answers the conflict's own text, any other rule its own.
        var unitOfWork = FailingStorage(Rule(kind));

        var result = await Topology(unitOfWork).CreateTerminalAsync(new CreateTerminalRequest(Guid.NewGuid(), "T1", "T1", "ABC"), CancellationToken.None);

        result.ErrorMessages.Should().Equal(Answer(kind));
        unitOfWork.Verify(u => u.PromiseNotToCommit(), Times.Once(), "a refused command keeps nothing");
    }

    [Theory]
    [InlineData("rule")]
    [InlineData("argument")]
    [InlineData("range")]
    public async Task RegisterAndCreateDraft_Should_AnswerTheRulesText_When_ADomainRuleIsRaisedAnywhere(string kind)
    {
        var request = new RegisterDeviceRequest("S-1", "StereoVision", "PC2SE", "HttpsPush", "Xovis", "Ntp", new DevicePlacement(Guid.NewGuid(), 20, 16, 5, 0, "Snake A"));
        var devices = FailingStorage(Rule(kind));
        var profiles = FailingStorage(Rule(kind));

        var registered = await Devices(devices).RegisterAsync(request, CancellationToken.None);
        var drafted = await ZoneProfiles(profiles).CreateDraftAsync(new CreateZoneProfileDraftRequest("ABC", null), CancellationToken.None);

        registered.ErrorMessages.Should().Equal(Answer(kind));
        drafted.ErrorMessages.Should().Equal(Answer(kind));
        devices.Verify(u => u.PromiseNotToCommit(), Times.Once());
        profiles.Verify(u => u.PromiseNotToCommit(), Times.Once());
    }

    [Theory]
    [InlineData("argument")]
    [InlineData("operation")]
    [InlineData("range")]
    public async Task Services_Should_LetTheExceptionThrough_When_ItIsNotADomainRule(string kind)
    {
        // The same base types, with a rule's wording, but not the domain's: never answered (CWE-209).
        Exception error = kind switch
        {
            "argument" => new ArgumentException("A name is required."),
            "operation" => new InvalidOperationException("Zone Q1 already exists here."),
            _ => new ArgumentOutOfRangeException("x", 1, "A physical capacity is 1 to 5,000 people.")
        };

        var request = new RegisterDeviceRequest("S-1", "StereoVision", "PC2SE", "HttpsPush", "Xovis", "Ntp", new DevicePlacement(Guid.NewGuid(), 20, 16, 5, 0, "Snake A"));
        var create = () => ZoneProfiles(FailingStorage(error)).CreateDraftAsync(new CreateZoneProfileDraftRequest("ABC", null), CancellationToken.None);
        var register = () => Devices(FailingStorage(error)).RegisterAsync(request, CancellationToken.None);
        var terminal = () => Topology(FailingStorage(error)).CreateTerminalAsync(new CreateTerminalRequest(Guid.NewGuid(), "T1", "T1", "ABC"), CancellationToken.None);

        (await create.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(error);
        (await register.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(error);
        (await terminal.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(error);
    }

    [Fact]
    public void DomainRules_Should_CarryTheirTextAndKind_When_TheEntitiesRefuse()
    {
        // The entities' own refusals are domain rules of the right kind, whose text carries no parameter or value suffix.
        var code = Caught(() => _ = new Airport("tq1", null, "Race", "Asia/Amman"));
        code.Should().BeAssignableTo<ArgumentException>().And.BeAssignableTo<IDomainRule>();
        ((IDomainRule)code).Rule.Should().Be("An IATA code is three upper case letters.");
        Caught(() => _ = new Airport("TQR", null, "   ", "Asia/Amman")).Should().BeAssignableTo<IDomainRule>("a blank name, refused by the entity itself rather than by a throw helper");

        var airport = new Airport("TQR", null, "Race", "Asia/Amman");
        var terminal = airport.AddTerminal("T1", "Terminal 1", "TQR");
        terminal.AddLevel("L0", "Ground", 0, 100, 80);
        var taken = (IDomainRule)Caught(() => airport.AddTerminal("T1", "Again", "TQR"));
        taken.Kind.Should().Be(DomainRuleKind.CodeTaken);
        ((IDomainRule)Caught(() => terminal.AddLevel("L0", "Again", 1, 100, 80))).Kind.Should().Be(DomainRuleKind.CodeTaken);
        var floor = (IDomainRule)Caught(() => terminal.AddLevel("L1", "Same floor", 0, 100, 80));
        floor.Kind.Should().Be(DomainRuleKind.FloorTaken);
        floor.Rule.Should().Be(TopologyErrors.FloorTaken);
        var range = Caught(() => terminal.AddLevel("L9", "High", 999, 100, 80));
        range.Should().BeOfType<DomainArgumentOutOfRangeException>();
        ((IDomainRule)range).Rule.Should().NotContain("Parameter").And.NotContain("Actual value").And.StartWith("A floor number is between");

        // A device's model, notes and queue zone name are checked by DisplayText, a domain helper whose refusals are rules too.
        var level = terminal.Levels.Single();
        Device Sensor(string model, string queueZoneName) => new("S01", DeviceFamily.StereoVision, model, DeviceTransport.HttpsPush, DeviceDialect.Xovis,
            ClockSource.Ntp, level, 20, 16, 5, 0, CoverageFootprint.Assumed(DeviceFamily.StereoVision, 5), queueZoneName, DateTime.UnixEpoch);
        ((IDomainRule)Caught(() => Sensor("Xovis\u0007", "Queue"))).Rule.Should().Be(DisplayText.UncleanMessage);
        ((IDomainRule)Caught(() => Sensor(new string('x', 101), "Queue"))).Rule.Should().Be("At most 100 characters.");
        ((IDomainRule)Caught(() => Sensor("Xovis", "Queue\u202EA"))).Rule.Should().Be(DisplayText.UncleanMessage);
        ((IDomainRule)Caught(() => Sensor("   ", "Queue"))).Rule.Should().Be(DisplayText.RequiredMessage);
        var sensor = Sensor("Xovis", "Queue");
        ((IDomainRule)Caught(() => sensor.UpdateDetails("Xovis\0", DeviceTransport.HttpsPush, DeviceDialect.Xovis, ClockSource.Ntp))).Rule
            .Should().Be(DisplayText.UncleanMessage);

        Caught(() => _ = Enum.Parse<DayOfWeek>("Someday")).Should().NotBeAssignableTo<IDomainRule>("the framework's");
        // DomainRuleTests holds every host assembly to the three sealed rule types, constructed by the domain model only.
    }

    #endregion
}
