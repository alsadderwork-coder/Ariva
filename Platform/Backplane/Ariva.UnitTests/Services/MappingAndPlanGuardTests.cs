using Ariva.Core.Domain.Common;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Security;
using Ariva.Core.Services;
using Ariva.Core.Services.Storage;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Topology;
using Ariva.Infra.Storage;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.UnitTests.Services;

/// <summary>
/// ARV-286 (CWE-209, CWE-20): the desk code mapping and floor plan services answer a domain rule by its type
/// (<see cref="IDomainRule"/>), as the topology, zone profile and device services do since ARV-282, and nothing else. A
/// refusal their entities make of a caller's value is a rule type with the text it had; an exception from outside the domain
/// (a driver's, a session's, a framework's), even an InvalidOperationException or an ArgumentException thrown where the
/// entity runs, passes through to the global handler's 500 without its text. Before, any InvalidOperationException's message
/// (desk code mappings, whose guard also covered the save and the audit entry) and any ArgumentException's message (floor
/// plans) was answered as a 400.
/// </summary>
public sealed class MappingAndPlanGuardTests
{
    #region Fields

    private const string Detail = "framework detail 4c1d";

    private const string Site = "DMO";

    #endregion

    #region Helpers

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Mock<ISiteScope> AllSites()
    {
        var scope = new Mock<ISiteScope>();
        scope.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SiteAccess(true, new HashSet<string>()));
        return scope;
    }

    private static Checkpoint Immigration() =>
        new Airport(Site, null, "Demo", "Asia/Amman").AddTerminal("T1", "Terminal 1", Site).AddLevel("L0", "Arrivals", 0, 300, 120)
            .AddCheckpoint("IMM", "Immigration", CheckpointKind.Immigration);

    private static Desk RealDesk()
    {
        var desk = Immigration().AddDesk("D01", null, DeskKind.Desk, ["CIT"]);
        desk.Id = Guid.NewGuid();
        return desk;
    }

    /// <summary>A desk with the real one's id and site, whose kind is <paramref name="kind"/> or, when it is null, whose kind's getter throws <paramref name="error"/>.</summary>
    private static Desk StandIn(Desk real, DeskKind? kind, Exception error = null)
    {
        var desk = new Mock<Desk> { CallBase = true };
        desk.Setup(d => d.Id).Returns(real.Id);
        desk.Setup(d => d.SiteCode).Returns(real.SiteCode);
        desk.Setup(d => d.Code).Returns(real.Code);
        if (kind is { } given)
            desk.Setup(d => d.Kind).Returns(given);
        else
            desk.Setup(d => d.Kind).Throws(error);
        return desk.Object;
    }

    private static SvcDeskCodeMappings Mappings(DeskCodeMapping mapping, Desk desk)
    {
        var storage = new Mock<IStorageProvider>();
        storage.Setup(s => s.GetAsync<DeskCodeMapping>(mapping.Id.GetValueOrDefault(), It.IsAny<CancellationToken>())).ReturnsAsync(mapping);
        storage.Setup(s => s.GetAsync<Desk>(desk.Id.GetValueOrDefault(), It.IsAny<CancellationToken>())).ReturnsAsync(desk);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.StorageProvider).Returns(storage.Object);
        var user = new Mock<ICurrentUser>().Object;
        return new SvcDeskCodeMappings(unitOfWork.Object, user, TimeProvider.System, AllSites().Object, new AuditTrail(unitOfWork.Object, user, TimeProvider.System),
            new FusionCache(new FusionCacheOptions(), new MemoryCache(new MemoryCacheOptions())));
    }

    private static DeskCodeMapping Mapping(Desk real)
    {
        var mapping = new DeskCodeMapping(ExternalSystem.Aman, "A-01", real);
        mapping.Id = Guid.NewGuid();
        return mapping;
    }

    /// <summary>A level of the site, whose site code reads as the site the first time (the service's own look-up) and then, when <paramref name="error"/> is given, throws it (in the entity, where the old guard caught it).</summary>
    private static Level LevelOf(Guid id, Exception error = null)
    {
        var level = new Mock<Level> { CallBase = true };
        level.Setup(l => l.Id).Returns(id);
        var siteCode = level.SetupSequence(l => l.SiteCode).Returns(Site);
        if (error is null)
            siteCode.Returns(Site).Returns(Site).Returns(Site);
        else
            siteCode.Throws(error);
        return level.Object;
    }

    private static SvcFloorPlans Plans(Level level)
    {
        var storage = new Mock<IStorageProvider>();
        storage.Setup(s => s.GetAsync<Level>(level.Id.GetValueOrDefault(), It.IsAny<CancellationToken>())).ReturnsAsync(level);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.StorageProvider).Returns(storage.Object);
        var user = new Mock<ICurrentUser>().Object;
        var scanner = new Mock<IMalwareScanner>();
        scanner.Setup(s => s.ScanAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync(MalwareScan.Clean);
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var screening = new UploadScreening(scanner.Object, Options.Create(new MalwareScanSettings { Host = "127.0.0.1" }), UploadScanPolicy.For("k8s-dev"),
            user, TimeProvider.System, scopes, NullLogger<UploadScreening>.Instance);
        return new SvcFloorPlans(unitOfWork.Object, user, TimeProvider.System, AllSites().Object, new Mock<IFileStorage>().Object,
            new AuditTrail(unitOfWork.Object, user, TimeProvider.System), screening);
    }

    private static FloorPlanUpload Upload(double metresPerPixel)
    {
        var png = PlanImages.Png(4, 3);
        return new FloorPlanUpload(new MemoryStream(png, writable: false), png.Length, "plan.png", metresPerPixel, 0, 0);
    }

    #endregion

    #region Entities

    [Theory]
    [InlineData(ExternalSystem.Aman, DeskKind.Desk, true)]
    [InlineData(ExternalSystem.Aman, DeskKind.EGate, true)]
    [InlineData(ExternalSystem.Aman, DeskKind.Counter, false)]
    [InlineData(ExternalSystem.Aodb, DeskKind.Counter, true)]
    [InlineData(ExternalSystem.Aodb, DeskKind.Desk, false)]
    [InlineData(ExternalSystem.Aodb, DeskKind.SecurityLane, false)]
    [InlineData((ExternalSystem)99, DeskKind.Desk, false)]
    public void Fits_Should_AnswerWhetherASystemsCodeCanMapTheDeskKind_When_TheImportChecksARowBeforeTheService(ExternalSystem system, DeskKind kind, bool fits)
    {
        DeskCodeMapping.Fits(system, kind).Should().Be(fits);
    }

    [Fact]
    public void DeskCodeMapping_Should_RefuseACallersValueWithTheRuleTypes_When_ItBreaksARule()
    {
        var real = RealDesk();
        var counter = Immigration().Level.AddCheckpoint("CHK", "Check-in", CheckpointKind.CheckIn).AddDesk("C01", null, DeskKind.Counter, []);

        var unknown = () => new DeskCodeMapping((ExternalSystem)99, "A-01", real);
        var badCode = () => new DeskCodeMapping(ExternalSystem.Aman, " ", real);
        var wrongKind = () => new DeskCodeMapping(ExternalSystem.Aman, "A-01", counter);
        var mapping = Mapping(real);
        real.SoftDelete("officer.one", DateTime.UtcNow);
        var deleted = () => mapping.Assign(real);

        unknown.Should().Throw<DomainArgumentOutOfRangeException>().Which.Rule.Should().Be("Unknown external system.");
        badCode.Should().Throw<DomainArgumentException>().Which.Rule.Should().Be("An external code is 1 to 32 letters, digits or . _ / - characters.");
        wrongKind.Should().Throw<DomainRuleException>().Which.Rule.Should().Be("A Aman code cannot map to a Counter.");
        deleted.Should().Throw<DomainRuleException>().Which.Rule.Should().Be("The desk is deleted.");
        var nullDesk = () => new DeskCodeMapping(ExternalSystem.Aman, "A-01", null);
        nullDesk.Should().Throw<ArgumentNullException>().Which.Should().NotBeAssignableTo<IDomainRule>("a null desk is the service's programming error, not a rule");
    }

    [Fact]
    public void DeskCodeMapping_Should_RefuseADeskOfAnotherSiteWithARule_When_ItIsReassigned()
    {
        var mapping = Mapping(RealDesk());
        var elsewhere = new Airport("OTH", null, "Other", "Asia/Amman").AddTerminal("T1", "Terminal 1", "OTH").AddLevel("L0", "Arrivals", 0, 300, 120)
            .AddCheckpoint("IMM", "Immigration", CheckpointKind.Immigration).AddDesk("D01", null, DeskKind.Desk, ["CIT"]);

        var move = () => mapping.Assign(elsewhere);

        move.Should().Throw<DomainRuleException>().Which.Rule.Should().Be("A mapping stays in its site.");
    }

    [Fact]
    public void FloorPlan_Should_RefuseACallersValueWithTheRuleTypes_When_ItBreaksARule()
    {
        var level = Immigration().Level;

        FloorPlan Plan(long size = 10, string name = "plan.png", double scale = 0.05, double originX = 0, string key = "k.png") =>
            new(level, key, "image/png", size, "ab", name, 4, 3, scale, originX, 0);

        ((Action)(() => Plan(size: 0))).Should().Throw<DomainArgumentOutOfRangeException>().Which.Rule.Should().Be("A floor plan is at most 20 MB.");
        ((Action)(() => Plan(name: new string('a', FloorPlan.MaxFileNameLength + 1)))).Should().Throw<DomainArgumentException>()
            .Which.Rule.Should().Be(FloorPlan.FileNameTooLongMessage);
        ((Action)(() => Plan(scale: 0))).Should().Throw<DomainArgumentOutOfRangeException>().Which.Rule.Should().Be("The scale is more than 0 and at most 10 metres per pixel.");
        ((Action)(() => Plan(originX: 2_000_000))).Should().Throw<DomainArgumentOutOfRangeException>().Which.Rule.Should().Be("The origin is a pixel within one million of the image.");
        ((Action)(() => Plan(key: " "))).Should().Throw<ArgumentException>().Which.Should()
            .NotBeAssignableTo<IDomainRule>("the storage key is generated by the service: a blank one is a programming error");
    }

    #endregion

    #region Desk code mappings

    [Fact]
    public async Task Update_Should_AnswerTheRulesText_When_TheDeskDoesNotFitTheSystem()
    {
        var real = RealDesk();
        var mapping = Mapping(real);

        var result = await Mappings(mapping, StandIn(real, DeskKind.Counter)).UpdateAsync(mapping.Id.GetValueOrDefault(), new UpdateDeskCodeMappingRequest(real.Id), Ct);

        result.ErrorMessages.Should().Equal("A Aman code cannot map to a Counter.");
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("argument")]
    public async Task Update_Should_LetTheExceptionThrough_When_ItIsNotTheDomains(string kind)
    {
        var real = RealDesk();
        var mapping = Mapping(real);
        Exception error = kind == "operation" ? new InvalidOperationException(Detail) : new ArgumentException(Detail);

        var update = () => Mappings(mapping, StandIn(real, null, error)).UpdateAsync(mapping.Id.GetValueOrDefault(), new UpdateDeskCodeMappingRequest(real.Id), Ct);

        (await update.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(error, "only a domain rule is answered; anything else is a 500 without its text");
    }

    /// <summary>
    /// The control for the test above: a rule thrown at the same place (the desk's kind, read inside the guarded call) is
    /// answered with its text, of either rule kind, so a let-through there is the filter's doing and not a throw outside the guard.
    /// </summary>
    [Theory]
    [InlineData("operation")]
    [InlineData("argument")]
    public async Task Update_Should_AnswerTheRule_When_ItIsThrownWhereTheOutsideExceptionWas(string kind)
    {
        var real = RealDesk();
        var mapping = Mapping(real);
        Exception rule = kind == "operation" ? new DomainRuleException("A rule of the desk 4c1d.") : new DomainArgumentException("A rule of the desk 4c1d.", "desk");

        var result = await Mappings(mapping, StandIn(real, null, rule)).UpdateAsync(mapping.Id.GetValueOrDefault(), new UpdateDeskCodeMappingRequest(real.Id), Ct);

        result.ErrorMessages.Should().Equal("A rule of the desk 4c1d.");
    }

    #endregion

    #region Floor plans

    [Fact]
    public async Task Upload_Should_AnswerTheRulesText_When_TheScaleBreaksTheRule()
    {
        var levelId = Guid.NewGuid();

        var result = await Plans(LevelOf(levelId)).UploadAsync(levelId, Upload(metresPerPixel: 0), Ct);

        result.ErrorMessages.Should().Equal("The scale is more than 0 and at most 10 metres per pixel.");
    }

    [Fact]
    public async Task Upload_Should_LetTheExceptionThrough_When_AnArgumentExceptionIsNotTheDomains()
    {
        var levelId = Guid.NewGuid();
        var error = new ArgumentException(Detail);

        var upload = () => Plans(LevelOf(levelId, error)).UploadAsync(levelId, Upload(metresPerPixel: 0.05), Ct);

        (await upload.Should().ThrowAsync<ArgumentException>()).Which.Should().BeSameAs(error, "an ArgumentException from outside the domain is not answered");
    }

    /// <summary>
    /// The control for the test above: a rule thrown at the same place (the level's site code, read the second time inside
    /// the entity's constructor) is answered with its text, so the let-through there is the filter's doing.
    /// </summary>
    [Fact]
    public async Task Upload_Should_AnswerTheRule_When_ItIsThrownWhereTheOutsideExceptionWas()
    {
        var levelId = Guid.NewGuid();

        var result = await Plans(LevelOf(levelId, new DomainArgumentException("A rule of the level 4c1d.", "level"))).UploadAsync(levelId, Upload(metresPerPixel: 0.05), Ct);

        result.ErrorMessages.Should().Equal("A rule of the level 4c1d.");
    }

    #endregion
}
