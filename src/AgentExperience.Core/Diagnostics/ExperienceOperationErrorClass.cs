namespace AgentExperience.Core.Diagnostics;

/// <summary>
/// The bounded classification a failed Experience operation is reported under. It is the only
/// failure detail that ever becomes a metric dimension, and it is deliberately four closed values:
/// an alert rule can be written against it once and never has to be widened, and no third-party
/// exception type name can turn a counter into an unbounded label set.
/// </summary>
/// <remarks>
/// <para>
/// <b>Content-free by construction.</b> A member name is all that is emitted -- never the exception's
/// message, its stack, its inner exceptions, or anything a driver put in them. A driver or HTTP
/// client message can quote SQL text, parameters, or caller data, so no exception object ever reaches
/// telemetry; the failing span carries this classification and the exception's <em>type name</em>
/// (<c>error.type</c>) and nothing else.
/// </para>
/// <para>
/// <b>It classifies, it does not diagnose.</b> The classification answers "is this worth paging
/// someone about, and who" -- not "what exactly went wrong". The typed results this library already
/// returns (<c>Outcome</c>, <c>Reason</c>, <c>Detail</c>, <c>Failure</c>) remain the place a host
/// looks for that, and they are available with no exporter and no listener registered at all.
/// </para>
/// </remarks>
public enum ExperienceOperationErrorClass
{
    /// <summary>
    /// The caller's own cancellation token was cancelled. Expected under load shedding and shutdown,
    /// and normally not alertable: the caller asked for this.
    /// </summary>
    Cancelled,

    /// <summary>
    /// A bound was exceeded and reported as a <see cref="TimeoutException"/>. Distinct from
    /// <see cref="Cancelled"/> because nobody asked for it, and distinct from
    /// <see cref="Infrastructure"/> because the dependency may be up and merely slow.
    /// </summary>
    Timeout,

    /// <summary>
    /// Storage, an embedding provider, or another infrastructure dependency failed -- including a
    /// cancellation the caller never requested, which is how a driver-side or HTTP-client timeout
    /// usually surfaces. This is the class a host's "my memory layer is down" alert watches.
    /// </summary>
    Infrastructure,

    /// <summary>
    /// Anything else: a programming error, a malformed request that reached a port, or a dependency
    /// throwing something this library does not recognize. A non-zero rate here is a bug to
    /// investigate, not a capacity signal.
    /// </summary>
    Unexpected,
}
