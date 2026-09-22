namespace AgentExperience.Core.Diagnostics;

/// <summary>
/// The closed set of <c>operation</c> dimension values this library emits, one per instrumented
/// call. They are literals rather than derived from a type or method name on purpose: a rename or a
/// refactor must not silently re-label an operator's dashboards, and the set a metric can be sliced
/// by has to be enumerable by reading one file.
/// </summary>
/// <remarks>
/// The span name for each is the value prefixed with <c>agentexperience.</c>, which is why the
/// operation and the span name never have to be kept in step by hand. Adding a value here is a
/// deliberate change: it widens the cardinality of every instrument at once.
/// </remarks>
internal static class ExperienceOperationNames
{
    /// <summary>Starting a captured run (<c>InMemoryExperienceCaptureService.StartRun</c>).</summary>
    public const string CaptureStartRun = "capture.start_run";

    /// <summary>Appending one attempt to a captured run (<c>InMemoryExperienceCaptureService.AppendAttemptAsync</c>).</summary>
    public const string CaptureAppendAttempt = "capture.append_attempt";

    /// <summary>Finalizing a captured run's completion (<c>InMemoryExperienceCaptureService.CompleteRunAsync</c>).</summary>
    public const string CaptureCompleteRun = "capture.complete_run";

    /// <summary>Aggregating evidence into a verification verdict (<c>VerificationAggregator.Aggregate</c>).</summary>
    public const string Verify = "verify";

    /// <summary>Turning an evaluated run into a reflection (<c>DefaultExperienceReflector.ReflectAsync</c>).</summary>
    public const string Reflect = "reflect";

    /// <summary>Turning a captured run into a durable Experience Record (<c>ExperienceFinalizationService.FinalizeAsync</c>).</summary>
    public const string Finalize = "finalize";

    /// <summary>Committing a lifecycle transition (<c>ExperienceLifecycleService.CommitAsync</c>).</summary>
    public const string LifecycleCommit = "lifecycle.commit";

    /// <summary>Applying one piece of confidence evidence (<c>ExperienceLifecycleService.ApplyEvidenceAsync</c>).</summary>
    public const string ConfidenceApply = "confidence.apply";

    /// <summary>Retrieving experience applicable to a task (<c>ExperienceRetrievalService.RetrieveAsync</c>).</summary>
    public const string Retrieve = "retrieve";

    /// <summary>Embedding one record's retrieval summary (<c>ExperienceIndexingService.IndexAsync</c>).</summary>
    public const string Index = "index";

    /// <summary>Removing one record's stored vector (<c>ExperienceIndexingService.RemoveAsync</c>).</summary>
    public const string Deindex = "deindex";

    /// <summary>Running one scoped re-index pass (<c>ExperienceIndexingService.ReindexAsync</c>).</summary>
    public const string Reindex = "reindex";

    /// <summary>Recording reuse feedback for a run (<c>ExperienceReuseFeedbackService.RecordAsync</c>).</summary>
    public const string ReuseFeedback = "reuse_feedback";
}
