using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ariva.Core.Messaging;

namespace Ariva.Infra.Messaging;

/// <summary>
/// The events Ariva publishes: each concrete <see cref="IEvent"/> with a <see cref="KafkaTopicAttribute"/>, by type and
/// by name. The outbox stores the name, the relay resolves it back, so a renamed class is a breaking change caught at
/// startup (unknown names stay in the outbox with an error rather than being dropped). Built once per process.
/// </summary>
public sealed class EventCatalog
{
    private readonly Dictionary<Type, string> _topics = [];
    private readonly Dictionary<string, Type> _types = new(StringComparer.Ordinal);

    /// <summary>JSON on the wire: plain camelCase documents, readable by the raw Confluent consumer and the Python worker.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public EventCatalog(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        foreach (var type in assemblies.Distinct().SelectMany(a => a.GetTypes()))
        {
            if (type.IsAbstract || !typeof(IEvent).IsAssignableFrom(type))
                continue;
            var topic = type.GetCustomAttribute<KafkaTopicAttribute>()?.Topic;
            if (topic is null)
                continue;
            Register(type, topic);
        }
    }

    public IReadOnlyCollection<Type> EventTypes => _topics.Keys;

    public void Register(Type type, string topic)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!KafkaTopics.IsKnown(topic))
            throw new InvalidOperationException($"{type.FullName} names topic '{topic}', which is not a KafkaTopics constant.");
        if (_types.TryGetValue(type.Name, out var other) && other != type)
            throw new InvalidOperationException($"Event names must be unique: {type.FullName} and {other.FullName} are both '{type.Name}'.");
        // The rider keeps one producer per topic, so a topic carries one event type (a new shape is a new versioned topic).
        var sharing = _topics.FirstOrDefault(t => t.Value == topic && t.Key != type).Key;
        if (sharing is not null)
            throw new InvalidOperationException($"Topic '{topic}' already carries {sharing.FullName}; {type.FullName} needs its own topic.");
        _topics[type] = topic;
        _types[type.Name] = type;
    }

    public string TopicOf(Type type) =>
        _topics.TryGetValue(type, out var topic)
            ? topic
            : throw new InvalidOperationException($"{type.FullName} has no [KafkaTopic]; it cannot be published (ADR-0018).");

    public bool TryResolve(string name, out Type type) => _types.TryGetValue(name ?? string.Empty, out type);
}
