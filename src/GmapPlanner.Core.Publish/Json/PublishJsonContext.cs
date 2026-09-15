using System.Text.Json.Serialization;
using GmapPlanner.Core.Services;

namespace GmapPlanner.Core.Json;

/// <summary>
/// Source-generated JSON type info for GmapPlanner.Core.Publish's own DTOs. Same trimming
/// rule as GmapPlannerJsonContext: reflection-based System.Text.Json is off in every build.
/// </summary>
[JsonSerializable(typeof(TimeSeriesResponse))]
internal partial class PublishJsonContext : JsonSerializerContext;
