namespace AgentExperience.Core.Reflections;

/// <summary>
/// Optionally implemented by an <see cref="IExperienceReflector"/> that shows a model only part of a run: it says
/// exactly which captured text it sent. Finalization's content guard admits a URL, hostname or IP address in a
/// model-authored reflection only when the same whole token appears in that text.
/// </summary>
/// <remarks>
/// A reflector that does not implement it is taken to have been given the task text (or the task ID when there is
/// none), every tool name, every tool call's and attempt's result and error, and the check IDs, in full. A
/// reflector that implements it and throws, or returns <see langword="null"/>, is taken to have been given nothing,
/// so every URL, hostname and IP address in its reflection is refused.
/// </remarks>
public interface IReflectionRunContent
{
    /// <summary>The captured text this reflector sends (or sent) to a model for <paramref name="request"/>, as plain, unescaped strings.</summary>
    /// <param name="request">The reflection request.</param>
    /// <returns>The captured strings, each as much of it as was sent.</returns>
    IReadOnlyList<string> GetReflectedRunContent(ReflectionRequest request);
}
