using System.Text.Json.Serialization;
using GmapPlanner.Core.Services;
using GmapPlanner.Core.Services.Gemini;

namespace GmapPlanner.Core.Json;

/// <summary>
/// Source-generated JSON type info for every type this app serializes.
///
/// The app publishes trimmed, which sets JsonSerializerIsReflectionEnabledByDefault=false —
/// the reflection-based JsonSerializer overloads throw InvalidOperationException at runtime
/// (in every build, not just published ones). Every serialize/deserialize call must go
/// through this context.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(GenerateContentResponse))]
[JsonSerializable(typeof(ExtractionResultDto))]
[JsonSerializable(typeof(UploadFileEnvelope))]
[JsonSerializable(typeof(UploadedFile))]
[JsonSerializable(typeof(PlacesSearchResponseDto))]
[JsonSerializable(typeof(GithubRelease))]
internal partial class GmapPlannerJsonContext : JsonSerializerContext;
