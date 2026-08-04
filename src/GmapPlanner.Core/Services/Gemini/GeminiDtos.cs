using System.Text.Json.Serialization;

namespace GmapPlanner.Core.Services.Gemini;

// -- generateContent response envelope --

internal record GenerateContentResponse
{
    [JsonPropertyName("candidates")] public List<Candidate>? Candidates { get; init; }
}

internal record Candidate
{
    [JsonPropertyName("content")] public Content? Content { get; init; }
    [JsonPropertyName("finishReason")] public string? FinishReason { get; init; }
}

internal record Content
{
    [JsonPropertyName("parts")] public List<Part>? Parts { get; init; }
}

internal record Part
{
    [JsonPropertyName("text")] public string? Text { get; init; }
}

// -- the itinerary JSON produced inside Part.Text, per our response schema --

internal record ExtractionResultDto
{
    [JsonPropertyName("trip_name")] public string? TripName { get; init; }
    [JsonPropertyName("days")] public List<DayDto>? Days { get; init; }
}

internal record DayDto
{
    [JsonPropertyName("day")] public int Day { get; init; }
    [JsonPropertyName("date")] public string? Date { get; init; }
    [JsonPropertyName("locations")] public List<LocationDto>? Locations { get; init; }
}

internal record LocationDto
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("lat")] public double Lat { get; init; }
    [JsonPropertyName("lng")] public double Lng { get; init; }
    [JsonPropertyName("notes")] public string? Notes { get; init; }
}

// -- Files API --

internal record UploadFileEnvelope
{
    [JsonPropertyName("file")] public UploadedFile? File { get; init; }
}

internal record UploadedFile
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("uri")] public string? Uri { get; init; }
    [JsonPropertyName("mimeType")] public string? MimeType { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
}
