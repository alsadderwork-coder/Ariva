using Ariva.Infra.NHibernate.Drivers;
using FluentAssertions;
using NHibernate.Connection;

namespace Ariva.UnitTests.Architecture;

/// <summary>
/// ARV-243 (CWE-400): NHibernate's connections are opened by Ariva's AsyncOpenConnectionProvider only. Its synchronous open is
/// sealed and the session factory refuses any other provider, but a type derived from it could still open synchronously in its
/// own asynchronous override; so no production type derives from it or implements NHibernate's provider interface.
/// </summary>
public sealed class ConnectionProviderTests
{
    [Fact]
    public void Platform_Should_DeclareNoOtherConnectionProvider_When_ItsAssembliesAreRead()
    {
        var offenders = Ariva.UnitTests.Setup.ArivaAssemblies.All
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsAbstract || !type.IsSealed)
            .Where(type => type != typeof(AsyncOpenConnectionProvider) && typeof(IConnectionProvider).IsAssignableFrom(type))
            .Select(type => type.FullName)
            .ToList();

        offenders.Should().BeEmpty("only AsyncOpenConnectionProvider opens NHibernate's connections");
    }

    [Fact]
    public void SynchronousOpen_Should_BeSealed_When_Inspected()
    {
        var open = typeof(AsyncOpenConnectionProvider).GetMethod(nameof(AsyncOpenConnectionProvider.GetConnection), [typeof(string)]);

        open.Should().NotBeNull();
        open.IsFinal.Should().BeTrue("a derived provider cannot bring the synchronous open back");
    }
}
