namespace GmapPlanner.Core.Errors;

/// <summary>
/// The Gemini call succeeded but its body is unusable (truncated, empty, wrong shape).
/// The one failure worth retrying: a transport/auth/quota error fails the same way
/// every time, so retrying it just doubles the wait and the input tokens.
/// </summary>
public class BadResponseException(string message) : PipelineException(message);
