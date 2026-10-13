namespace AgentExperience.MicrosoftAgentFramework;

/// <summary>
/// A person's decision on a run, as the host's own approval flow recorded it. Returned by the delegate given to
/// <see cref="ExperienceVerifiers.Approval"/>.
/// </summary>
/// <param name="ApproverId">
/// Who decided: the approver's identifier from the host's own authentication. It is stored verbatim as the evidence's
/// producer, so use an opaque identifier (a user's object ID), not an email address or a name. It must not be blank.
/// </param>
/// <param name="Approved">Whether the person approved the run's result.</param>
public sealed record ExperienceApproval(string ApproverId, bool Approved);
