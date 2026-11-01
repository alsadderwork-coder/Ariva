using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Security;
using Ariva.Core.Services;
using Ariva.Core.Services.Sensing;
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
/// leaves the unit of work promised not to commit, so its end does not send the aborted transaction again.
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

    [Fact]
    public void FromDomain_Should_TellTheDomainsRulesFromOtherExceptions_When_Read()
    {
        SvcTopology.FromDomain(Caught(() => _ = new Airport("tq1", null, "Race", "Asia/Amman"))).Should().BeTrue("a code the entity refuses");
        SvcTopology.FromDomain(Caught(() => _ = new Airport("TQR", null, "   ", "Asia/Amman"))).Should().BeTrue("a blank name, refused by the entity itself rather than by a throw helper");
        SvcTopology.FromDomain(Caught(() => _ = Enum.Parse<DayOfWeek>("Someday"))).Should().BeFalse("the framework's");
        SvcTopology.FromDomain(new ArgumentException(Detail)).Should().BeFalse("never thrown, so from nowhere");
        SvcTopology.FromDomain(null).Should().BeFalse();
    }

    #endregion
}
