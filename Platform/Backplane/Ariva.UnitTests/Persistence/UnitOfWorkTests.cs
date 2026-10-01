using Ariva.Infra.Services.Foundation;
using Ariva.UnitTests.Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ariva.UnitTests.Persistence;

/// <summary>ARV-005: commit and rollback rules of the ported unit of work, and the outbox hand-off (ADR-0018).</summary>
public sealed class UnitOfWorkTests
{
    private readonly FakeStorageProvider _storage = new();
    private readonly FakeOutbox _outbox;
    private readonly UnitOfWork _unitOfWork;

    public UnitOfWorkTests()
    {
        _outbox = new FakeOutbox(_storage);
        _unitOfWork = new UnitOfWork(_storage, _outbox, NullLogger<UnitOfWork>.Instance);
    }

    [Fact]
    public async Task EndAsync_Should_FlushWriteEventsThenCommit_When_PromisedToCommit()
    {
        var zone = new SampleZone();
        zone.RaiseDomainEvent(new SampleZoneOpened());
        _storage.TrackedEntities.Add(zone);
        _unitOfWork.PromiseToCommit();

        await _unitOfWork.EndAsync();

        _storage.Calls.Should().Equal("begin", "flush", "outbox:1", "flush", "commit");
        _outbox.Written.Should().ContainSingle().Which.Should().BeOfType<SampleZoneOpened>();
        zone.DomainEvents.Should().BeEmpty("events are dequeued so a second commit cannot send them again");
    }

    [Fact]
    public async Task EndAsync_Should_SkipOutbox_When_NoEntityRaisedEvents()
    {
        _storage.TrackedEntities.Add(new SampleZone());
        _unitOfWork.PromiseToCommit();

        await _unitOfWork.EndAsync();

        _storage.Calls.Should().Equal("begin", "flush", "commit");
    }

    [Fact]
    public async Task EndAsync_Should_RollBack_When_NotPromisedToCommit()
    {
        _storage.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);

        await _unitOfWork.EndAsync();

        _storage.Calls.Should().Equal("begin", "rollback");
    }

    [Fact]
    public async Task EndAsync_Should_RollBackAndRethrow_When_CommitFails()
    {
        var rolledBack = false;
        _storage.CommitFailure = new InvalidOperationException("serialization failure");
        _unitOfWork.RegisterPostRollbackAction(() =>
        {
            rolledBack = true;
            return Task.CompletedTask;
        });
        _unitOfWork.PromiseToCommit();

        var act = () => _unitOfWork.EndAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("serialization failure");
        _storage.Calls.Should().EndWith(["commit", "rollback"]);
        rolledBack.Should().BeTrue();
        _unitOfWork.IsToBeCommitted.Should().BeFalse();
    }

    [Fact]
    public async Task EndAsync_Should_RunEveryPostCommitAction_When_OneThrows()
    {
        var ran = new List<int>();
        _unitOfWork.RegisterPostCommitAction(() => throw new InvalidOperationException("cache down"));
        _unitOfWork.RegisterPostCommitAction(() =>
        {
            ran.Add(2);
            return Task.CompletedTask;
        });
        _unitOfWork.PromiseToCommit();

        await _unitOfWork.EndAsync();

        ran.Should().Equal(2);
        _storage.Calls.Should().Contain("commit");
    }

    [Fact]
    public async Task PromiseNotToCommit_Should_CancelEarlierPromise_When_Called()
    {
        _storage.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);
        _unitOfWork.PromiseToCommit();
        _unitOfWork.PromiseNotToCommit();

        await _unitOfWork.EndAsync();

        _storage.Calls.Should().NotContain("commit");
    }

    [Fact]
    public async Task DisposeAsync_Should_EndUnit_When_NotEndedExplicitly()
    {
        _unitOfWork.PromiseToCommit();

        await _unitOfWork.DisposeAsync();

        _storage.Calls.Should().Contain("commit");
    }

    [Fact]
    public async Task WriteAsync_Should_Throw_When_EventsRaisedWithoutOutbox()
    {
        var outbox = new MissingDomainEventOutbox();

        var withEvents = () => outbox.WriteAsync(_storage, [new SampleZoneOpened()]);
        var withoutEvents = () => outbox.WriteAsync(_storage, []);

        await withEvents.Should().ThrowAsync<InvalidOperationException>().WithMessage("*SampleZoneOpened*ARV-020*");
        await withoutEvents.Should().NotThrowAsync();
    }
}
