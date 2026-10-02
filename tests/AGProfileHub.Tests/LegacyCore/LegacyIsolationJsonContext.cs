using System.Text.Json.Serialization;
namespace AGProfileHub.Core;
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IsolationStateDocument))]
[JsonSerializable(typeof(IsolationScopeMarker))]
internal partial class LegacyIsolationJsonContext : JsonSerializerContext;
