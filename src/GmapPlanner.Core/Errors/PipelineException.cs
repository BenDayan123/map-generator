namespace GmapPlanner.Core.Errors;

/// <summary>
/// User-facing pipeline failure (bad file, API error, no locations, ...).
/// Thrown instead of exiting so callers (CLI, GUI) can present the message themselves.
/// </summary>
public class PipelineException : Exception
{
    public PipelineException(string message) : base(message) { }
    public PipelineException(string message, Exception inner) : base(message, inner) { }
}
