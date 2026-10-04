using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Ariva.Core.Messaging;
using Ariva.Core.Sensing;
using Ariva.Infra.Messaging;

namespace Ariva.UnitTests.Messaging.AsyncApi;

/// <summary>
/// Builds docs/architecture/asyncapi.yaml (ARV-067) from the code: the topics from KafkaTopics, their retention from
/// TopicCatalog, each Ariva topic's payload type from its [KafkaTopic] attribute, every payload schema from the type with
/// the serializer options used on the wire (System.Text.Json's schema exporter), producers, consumers and keys from
/// <see cref="TopicDescriptions"/>. The same model renders the topic table of wiki/08-Integration-Guide.md.
/// </summary>
public static class AsyncApiDocument
{
    public const string SchemaVersion = "1.0.0";
    private const string KafkaBinding = "0.5.0";
    private const string DeadLetterChannel = "deadLetters";

    private static readonly string[] OutboxHeaders = ["ariva-event-type", "ariva-event-version", "ariva-correlation-id", "ariva-causation-id"];

    /// <summary>The Ariva event type of each topic, from the [KafkaTopic] attributes in Ariva.Core (test probes excluded).</summary>
    public static IReadOnlyDictionary<string, Type> EventTypes { get; } = typeof(KafkaTopics).Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetCustomAttribute<KafkaTopicAttribute>() is not null)
        .ToDictionary(t => t.GetCustomAttribute<KafkaTopicAttribute>()!.Topic, t => t, StringComparer.Ordinal);

    public static Type PayloadOf(string topic) =>
        EventTypes.TryGetValue(topic, out var type) ? type : TopicDescriptions.All[topic].Payload;

    public static JsonObject Build()
    {
        var defaults = new TopicDefaults();
        var specs = TopicCatalog.ForProvisioning(new TopicDefaults { ProvisionAmanFeed = true }).ToDictionary(s => s.Name, StringComparer.Ordinal);
        var channels = new JsonObject();
        var operations = new JsonObject();
        var messages = new JsonObject();
        var schemas = new JsonObject();

        foreach (var topic in KafkaTopics.All)
        {
            var description = TopicDescriptions.All[topic];
            var id = ChannelId(topic);
            var payload = PayloadOf(topic);
            var spec = specs[topic];
            var channel = new JsonObject
            {
                ["address"] = topic,
                ["title"] = topic,
                ["description"] = description.Reserved ? $"Reserved: declared in KafkaTopics and provisioned, with no producer or consumer yet. {description.Summary}" : description.Summary
            };
            var aman = !topic.StartsWith("ariva.", StringComparison.Ordinal);
            if (payload is not null)
            {
                var name = payload.Name;
                messages[name] = Message(payload, description, aman);
                schemas[name] = Schema(payload, aman);
                channel["messages"] = new JsonObject { [name] = new JsonObject { ["$ref"] = $"#/components/messages/{name}" } };
            }

            if (aman)
                channel["description"] += " The topic is AMAN's in a shared cluster; Ariva creates it only where the simulator plays AMAN (Kafka:Topics:ProvisionAmanFeed).";
            channel["bindings"] = new JsonObject { ["kafka"] = TopicBinding(spec) };
            channel["x-ariva-retention"] = Retention(spec);
            channels[id] = channel;

            if (payload is null)
                continue;
            if (description.Producer is { } producer)
            {
                operations[$"{Camel(producer.Split(',')[0].Trim())}Sends{Pascal(id)}"] = Operation("send", id, payload.Name,
                    aman ? "AMAN publishes the record (raw JSON value, string key, no headers)." : $"Produced by {producer} through the transactional outbox or directly (Ingest); value is the event as JSON, key as below.", null);
            }

            foreach (var consumer in description.Consumers)
                operations[$"{consumer.Service}Receives{Pascal(id)}{Pascal(consumer.Purpose)}"] = Operation("receive", id, payload.Name, consumer.Description, consumer.GroupId);
        }

        channels[DeadLetterChannel] = DeadLetters(specs);
        messages[nameof(Ariva.Infra.Messaging.Kafka.DeadLetter)] = new JsonObject
        {
            ["name"] = nameof(Ariva.Infra.Messaging.Kafka.DeadLetter),
            ["title"] = "Dead letter",
            ["summary"] = "A record a consumer could not read or handle after its retries, with the original bytes (base64, at most 512 KB), headers and the error.",
            ["contentType"] = "application/json",
            ["payload"] = new JsonObject { ["$ref"] = "#/components/schemas/DeadLetter" },
            ["bindings"] = new JsonObject { ["kafka"] = new JsonObject { ["key"] = new JsonObject { ["type"] = "string", ["description"] = "The source record's key, or none." }, ["bindingVersion"] = KafkaBinding } }
        };
        schemas[nameof(Ariva.Infra.Messaging.Kafka.DeadLetter)] = Schema(typeof(Ariva.Infra.Messaging.Kafka.DeadLetter), aman: false);
        operations["consumersSendDeadLetters"] = Operation("send", DeadLetterChannel, nameof(Ariva.Infra.Messaging.Kafka.DeadLetter),
            "Every consumer writes what it cannot handle to the dead-letter topic of the source topic (ADR-0018); nothing consumes them, operators inspect and replay.", null);

        return new JsonObject
        {
            ["asyncapi"] = "3.0.0",
            ["info"] = new JsonObject
            {
                ["title"] = "Ariva Kafka topics",
                ["version"] = SchemaVersion,
                ["description"] =
                    "Every Kafka topic Ariva produces or consumes, and AMAN's feed (ARV-067). Generated from the code by AsyncApiTests: topics from KafkaTopics, " +
                    "retention from TopicCatalog, payload schemas from the message types with the serializer options used on the wire. Do not edit by hand: " +
                    "run the test with ARIVA_UPDATE_ASYNCAPI=1 after changing a topic, a message type or TopicDescriptions. Values are JSON (camelCase, enums " +
                    "by name, nulls written, UTC times with Z); keys are UTF-8 strings. Ariva topics follow ariva.<context>.<event>.v<n> (ADR-0019)."
            },
            ["defaultContentType"] = "application/json",
            ["servers"] = new JsonObject
            {
                ["site"] = new JsonObject
                {
                    ["host"] = "kafka:9092",
                    ["protocol"] = "kafka",
                    ["description"] = $"The site's Kafka cluster (KRaft). SASL over TLS in production; plaintext only in vm-local and k8s-dev. Default {defaults.Partitions} partitions per topic, replication {defaults.ReplicationFactor}, min.insync.replicas {defaults.MinInSyncReplicas}, 1 MB per message."
                }
            },
            ["channels"] = channels,
            ["operations"] = operations,
            ["components"] = new JsonObject { ["messages"] = messages, ["schemas"] = schemas }
        };
    }

    private static JsonObject Message(Type payload, TopicDescription description, bool aman)
    {
        var message = new JsonObject
        {
            ["name"] = payload.Name,
            ["title"] = payload.Name,
            ["summary"] = description.Summary,
            ["contentType"] = "application/json",
            ["payload"] = new JsonObject { ["$ref"] = $"#/components/schemas/{payload.Name}" }
        };
        if (!aman)
        {
            var headers = new JsonObject();
            foreach (var header in OutboxHeaders)
                headers[header] = new JsonObject { ["type"] = "string" };
            message["headers"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "Set by the outbox and the bus: the event's class name and schema version, and the correlation and causation ids when there are any.",
                ["properties"] = headers
            };
        }

        message["bindings"] = new JsonObject
        {
            ["kafka"] = new JsonObject
            {
                ["key"] = new JsonObject { ["type"] = "string", ["description"] = description.Key.Description },
                ["bindingVersion"] = KafkaBinding
            }
        };
        return message;
    }

    private static JsonObject Operation(string action, string channel, string message, string description, string group)
    {
        var operation = new JsonObject
        {
            ["action"] = action,
            ["channel"] = new JsonObject { ["$ref"] = $"#/channels/{channel}" },
            ["description"] = description,
            ["messages"] = new JsonArray(new JsonObject { ["$ref"] = $"#/channels/{channel}/messages/{message}" })
        };
        if (group is not null)
        {
            operation["bindings"] = new JsonObject
            {
                ["kafka"] = new JsonObject { ["groupId"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(group) }, ["bindingVersion"] = KafkaBinding }
            };
        }

        return operation;
    }

    private static JsonObject TopicBinding(TopicSpec spec)
    {
        var configuration = new JsonObject { ["cleanup.policy"] = new JsonArray(spec.Compacted ? "compact" : "delete") };
        if (spec.RetentionDays is { } days)
            configuration["retention.ms"] = TimeSpan.FromDays(days).Ticks / TimeSpan.TicksPerMillisecond;
        configuration["max.message.bytes"] = 1_048_576;
        var binding = new JsonObject
        {
            ["topic"] = spec.Name,
            ["partitions"] = spec.Partitions,
            ["replicas"] = spec.ReplicationFactor,
            ["topicConfiguration"] = configuration,
            ["bindingVersion"] = KafkaBinding
        };
        return binding;
    }

    private static string Retention(TopicSpec spec) =>
        spec.Compacted ? "compacted" : $"{spec.RetentionDays} days";

    private static JsonObject DeadLetters(IReadOnlyDictionary<string, TopicSpec> specs)
    {
        var sources = new JsonArray();
        foreach (var topic in KafkaTopics.All)
            sources.Add(KafkaTopics.DeadLetter(topic)["ariva.".Length..^KafkaTopics.DeadLetterSuffix.Length]);
        var example = specs[KafkaTopics.DeadLetter(KafkaTopics.DeviceTrackSample)];
        return new JsonObject
        {
            ["address"] = "ariva.{source}" + KafkaTopics.DeadLetterSuffix,
            ["title"] = "Dead-letter topics",
            ["description"] = "One per topic: <topic>.dlq.v1, with AMAN's feed topics under Ariva's prefix (ariva.aman.feed.<contract>.v1.dlq.v1), so every dead letter stays under Ariva's ACLs.",
            ["parameters"] = new JsonObject
            {
                ["source"] = new JsonObject { ["description"] = "The source topic without the ariva. prefix.", ["enum"] = sources }
            },
            ["messages"] = new JsonObject { ["DeadLetter"] = new JsonObject { ["$ref"] = "#/components/messages/DeadLetter" } },
            ["bindings"] = new JsonObject
            {
                ["kafka"] = new JsonObject
                {
                    ["partitions"] = example.Partitions,
                    ["replicas"] = example.ReplicationFactor,
                    ["topicConfiguration"] = new JsonObject
                    {
                        ["cleanup.policy"] = new JsonArray("delete"),
                        ["retention.ms"] = TimeSpan.FromDays(example.RetentionDays!.Value).Ticks / TimeSpan.TicksPerMillisecond,
                        ["max.message.bytes"] = 1_048_576
                    },
                    ["bindingVersion"] = KafkaBinding
                }
            },
            ["x-ariva-retention"] = Retention(example)
        };
    }

    /// <summary>The JSON Schema of a payload as it is on the wire, from the type and the wire's serializer options.</summary>
    private static JsonNode Schema(Type type, bool aman)
    {
        // Ariva's topics use EventCatalog.Json; AMAN's records are written the same way (camelCase, enums by name) and
        // read strictly by Ariva (AmanFeedJson).
        var options = new JsonSerializerOptions(EventCatalog.Json) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.MakeReadOnly();
        var exporter = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = aman,
            TransformSchemaNode = (context, node) =>
            {
                // A [Flags] enum is written as comma-separated names, not one of them.
                if (context.TypeInfo.Type == typeof(SensedFlags) && node is JsonObject flags)
                {
                    flags.Remove("enum");
                    flags["type"] = "string";
                    flags["description"] = "Comma-separated SensedFlags names (Skewed, Corrected, ClockUnreliable), None for none.";
                }

                // Ariva reads AMAN's records strictly (an unknown member is dead-lettered), so the contract says so.
                if (aman && context.TypeInfo.Kind == JsonTypeInfoKind.Object && node is JsonObject shape && shape.ContainsKey("properties"))
                    shape["additionalProperties"] = false;

                return node;
            }
        };
        var schema = options.GetJsonSchemaAsNode(type, exporter);
        if (schema is JsonObject root)
        {
            root.Remove("$schema");
            root["title"] = type.Name;
            root["x-ariva-type"] = type.FullName;
        }

        return schema;
    }

    public static string ChannelId(string topic)
    {
        var field = typeof(KafkaTopics).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Single(f => f.IsLiteral && f.FieldType == typeof(string) && (string)f.GetRawConstantValue() == topic);
        return char.ToLowerInvariant(field.Name[0]) + field.Name[1..];
    }

    private static string Pascal(string text)
    {
        var parts = text.Split(['-', '.', ' '], StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static string Camel(string text)
    {
        var pascal = Pascal(text);
        return char.ToLowerInvariant(pascal[0]) + pascal[1..];
    }

    /// <summary>The wiki's topic table (wiki/08-Integration-Guide.md, between its generated-table markers).</summary>
    public static string WikiTable()
    {
        var specs = TopicCatalog.ForProvisioning(new TopicDefaults { ProvisionAmanFeed = true }).ToDictionary(s => s.Name, StringComparer.Ordinal);
        var text = new StringBuilder();
        text.Append("| Topic | Payload | Key | Producer | Consumer groups | Retention |\n|---|---|---|---|---|---|\n");
        foreach (var topic in KafkaTopics.All)
        {
            var description = TopicDescriptions.All[topic];
            var payload = PayloadOf(topic);
            var groups = description.Consumers.Count == 0 ? (description.Reserved ? "reserved" : "none yet") : string.Join(", ", description.Consumers.Select(c => $"`{c.GroupId}`").Distinct());
            var key = description.Key?.Label ?? "";
            text.Append(CultureInfo.InvariantCulture, $"| `{topic}` | {(payload is null ? "" : $"`{payload.Name}`")} | {Cell(key)} | {description.Producer ?? ""} | {groups} | {Retention(specs[topic])} |\n");
        }

        return text.ToString();
    }

    // A table cell: a pipe would end the cell, and <site> would read as an HTML tag.
    private static string Cell(string text) => text
        .Replace("|", "/", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>YAML 1.2 for the document: block style, every string double-quoted (JSON escapes are valid YAML).</summary>
    public static string ToYaml(JsonNode node)
    {
        var text = new StringBuilder();
        text.Append("# Generated by Platform/Backplane/Ariva.UnitTests/Messaging/AsyncApi (ARV-067). Do not edit: run AsyncApiTests with\n");
        text.Append("# ARIVA_UPDATE_ASYNCAPI=1 to regenerate, and validate with npx @asyncapi/cli validate.\n");
        Write(text, node, 0, inList: false);
        return text.ToString();
    }

    private static void Write(StringBuilder text, JsonNode node, int indent, bool inList)
    {
        var pad = new string(' ', indent);
        switch (node)
        {
            case JsonObject map when map.Count == 0:
                text.Append(" {}\n");
                break;
            case JsonObject map:
                if (!inList)
                    text.Append('\n');
                var first = true;
                foreach (var (key, value) in map)
                {
                    text.Append(inList && first ? "" : pad).Append(Key(key)).Append(':');
                    first = false;
                    Write(text, value, indent + 2, inList: false);
                }

                break;
            case JsonArray list when list.Count == 0:
                text.Append(" []\n");
                break;
            case JsonArray list:
                text.Append('\n');
                foreach (var item in list)
                {
                    text.Append(pad).Append("- ");
                    if (item is JsonObject { Count: > 0 } itemMap)
                        Write(text, itemMap, indent + 2, inList: true);
                    else if (item is JsonObject or JsonArray)
                        Write(text, item, indent + 2, inList: false);
                    else
                        text.Append(Scalar(item)).Append('\n');
                }

                break;
            default:
                text.Append(' ').Append(Scalar(node)).Append('\n');
                break;
        }
    }

    private static string Key(string key) =>
        System.Text.RegularExpressions.Regex.IsMatch(key, @"^[A-Za-z$_][A-Za-z0-9_.$-]*$") && !IsReserved(key) ? key : Quoted(key);

    private static bool IsReserved(string key) =>
        key.ToLowerInvariant() is "true" or "false" or "null" or "yes" or "no" or "on" or "off" or "y" or "n" or "~";

    private static string Scalar(JsonNode node) => node switch
    {
        null => "null",
        JsonValue value when value.TryGetValue<string>(out var text) => Quoted(text),
        _ => node.ToJsonString()
    };

    /// <summary>A YAML double-quoted scalar: backslash, quote and control characters escaped, everything else as it is.</summary>
    private static string Quoted(string text)
    {
        var quoted = new StringBuilder("\"");
        foreach (var c in text)
        {
            quoted.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\t' => "\\t",
                _ when char.IsControl(c) => $"\\u{(int)c:X4}",
                _ => c.ToString()
            });
        }

        return quoted.Append('"').ToString();
    }
}
