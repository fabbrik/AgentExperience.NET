using AgentExperience.Abstractions;

namespace AgentExperience.Core.Capture;

/// <summary>
/// How the capture service keeps completed runs in an <see cref="IExperienceRunStore"/>. Register one as a singleton
/// beside <c>AddAgentExperienceCore</c> (before or after it) to change the defaults; without one, every completed run is
/// saved and a save may take up to <see cref="DefaultSaveTimeout"/>.
/// </summary>
/// <remarks>
/// A stored run is the run as capture holds it: its attempts sanitized, and its <see cref="ExperienceRun.TaskId"/>,
/// <see cref="ExperienceRun.TaskDescription"/>, <see cref="ExperienceRun.Environment"/> and
/// <see cref="ExperienceRun.Provenance"/> exactly as the host supplied them to <c>StartRun</c>, which sanitizes none of
/// them. Keep anything those must not hold out of them, or keep the run out of the store with
/// <see cref="ShouldPersistRun"/>.
/// </remarks>
public sealed class ExperienceRunStoreOptions
{
    /// <summary>The <see cref="SaveTimeout"/> used when none is set.</summary>
    public static readonly TimeSpan DefaultSaveTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Decides, for each completed run, whether it is saved. <see langword="null"/> (the default) saves every one. It runs
    /// on the completing call, after the run is completed; a predicate that throws is reported as a run store failure and
    /// the run is not saved.
    /// </summary>
    public Func<ExperienceRun, bool>? ShouldPersistRun { get; init; }

    /// <summary>
    /// How long the completing call waits for a save before it reports the save as failed (the run still completes; the
    /// save itself runs off the caller's thread and may still land). Default <see cref="DefaultSaveTimeout"/>; must be
    /// positive.
    /// </summary>
    public TimeSpan SaveTimeout
    {
        get;
        init => field = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(SaveTimeout), value, "Must be positive.");
    } = DefaultSaveTimeout;
}
