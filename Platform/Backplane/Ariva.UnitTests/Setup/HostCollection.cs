namespace Ariva.UnitTests.Setup;

/// <summary>
/// Test classes that boot hosts with WebApplicationFactory run one after another: the factory finds each host
/// through process wide diagnostic events, and sequential runs keep host start up deterministic.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HostCollection
{
    /// <summary>The collection name used in <c>[Collection(HostCollection.Name)]</c>.</summary>
    public const string Name = "Ariva hosts";
}
