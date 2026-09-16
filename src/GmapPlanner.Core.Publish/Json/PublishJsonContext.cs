using System.Text.Json.Serialization;
using GmapPlanner.Core.Services;
using GmapPlanner.Core.Services.Publish;

namespace GmapPlanner.Core.Json;

/// <summary>
/// Source-generated JSON type info for GmapPlanner.Core.Publish's own DTOs. Same trimming
/// rule as GmapPlannerJsonContext: reflection-based System.Text.Json is off in every build.
/// CamelCase policy matches the on-the-wire job JSON; TimeSeriesResponse pins its own names
/// via [JsonPropertyName], which override the policy.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TimeSeriesResponse))]
[JsonSerializable(typeof(SessionFile))]
[JsonSerializable(typeof(JobPayload))]
[JsonSerializable(typeof(JobStatus))]
internal partial class PublishJsonContext : JsonSerializerContext;
