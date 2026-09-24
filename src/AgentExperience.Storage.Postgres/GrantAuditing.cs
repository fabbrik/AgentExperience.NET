using AgentExperience.Abstractions;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// The one place this adapter family turns "a grant delivered these records" into access rows and
/// decides what a failed append does to the read. The record store, the text candidate source, and the
/// vectors index all go through it, so the three audited channels cannot drift apart on the mode, on
/// the blank-principal rule, or on what a cancelled append means.
/// </summary>
internal static class GrantAuditing
{
    /// <summary>
    /// Builds the row for one delivered record. Everything on it was decided by the read that already
    /// happened; nothing here re-derives whether a grant applied.
    /// </summary>
    /// <param name="auditing">The host's policy, which also carries the clock the row is stamped from.</param>
    /// <param name="authorization">
    /// The host-established context the read ran under. The principal comes from here and never from
    /// anything the caller passed as data -- the same rule a lifecycle event's actor follows.
    /// </param>
    /// <param name="requestScope">The scope the read was made in: the recipient the grant permits.</param>
    /// <param name="correlationId">The host's identifier for the work that caused the read, if any.</param>
    /// <param name="record">The record as it was delivered, which is where the revision comes from.</param>
    /// <param name="permittingGrantId">
    /// The grant the statement used. <see cref="Guid.Empty"/> when the reader could not name one, which
    /// its own SQL makes impossible -- and if it ever happened, the database's
    /// <c>experience_grant_access_grant_id_not_empty</c> refuses the row and the mode decides the read,
    /// rather than a shared record being delivered with no trail.
    /// </param>
    /// <param name="disclosure">
    /// The permitting grant's disclosure level, from the same lateral row that named it. Recorded as the
    /// level at delivery; <see langword="null"/> only if the reader could not read one, in which case
    /// the database's <c>experience_grant_access_disclosure_recorded</c> refuses the row and the mode
    /// decides the read.
    /// </param>
    public static ExperienceGrantAccess Access(
        ExperienceGrantAuditing auditing,
        AuthorizationContext authorization,
        Scope requestScope,
        string? correlationId,
        ExperienceRecord record,
        Guid? permittingGrantId,
        ExperienceGrantDisclosure? disclosure) =>
        new(
            AccessId: Guid.NewGuid(),
            GrantId: permittingGrantId ?? Guid.Empty,
            ExperienceId: record.ExperienceId,
            RecordRevision: record.Revision,
            RecordScope: record.Scope,
            RecipientScope: requestScope,
            PrincipalId: authorization.PrincipalId,
            CorrelationId: string.IsNullOrWhiteSpace(correlationId) ? null : correlationId,
            OccurredAt: auditing.Clock.GetUtcNow(),
            Disclosure: disclosure);

    /// <summary>
    /// Appends every row a single read produced, in one statement, and says whether the read may hand
    /// back what it found.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A blank principal never reaches the database. A row that cannot say <em>who</em> read the record
    /// does not answer the question this ledger exists for, so it is treated as a failed append: it is
    /// reported like any other, and under <see cref="ExperienceGrantAuditingMode.Required"/> the read
    /// fails closed. That is a deliberate obligation on the host -- establish a principal, or do not
    /// require auditing.
    /// </para>
    /// <para>
    /// Cancellation arriving during the append is a failure like any other rather than an exception
    /// thrown over the top of a completed read. The records were already read; whether they may be
    /// returned is the mode's decision, and letting a late cancellation discard them under best effort
    /// would contradict the mode the host chose.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> when the read may return its records: the rows landed, there were none to
    /// write, or the write failed under <see cref="ExperienceGrantAuditingMode.BestEffort"/>.
    /// <see langword="false"/> only when a write failed under
    /// <see cref="ExperienceGrantAuditingMode.Required"/>.
    /// </returns>
    public static async Task<bool> RecordAsync(
        ExperienceGrantAuditing auditing,
        IReadOnlyList<ExperienceGrantAccess> accesses,
        CancellationToken cancellationToken)
    {
        if (accesses.Count == 0)
        {
            // Nothing was delivered through a grant: an owner's own records, or nothing at all.
            return true;
        }

        for (var i = 0; i < accesses.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(accesses[i].PrincipalId))
            {
                return Fail(
                    auditing,
                    accesses,
                    new ExperienceStoreException(
                        "A grant-permitted read cannot be recorded without a principal: the host's AuthorizationContext.PrincipalId is blank."));
            }
        }

        try
        {
            await auditing.Log.RecordAsync(accesses, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            return Fail(auditing, accesses, ex);
        }
    }

    private static bool Fail(
        ExperienceGrantAuditing auditing,
        IReadOnlyList<ExperienceGrantAccess> accesses,
        Exception failure)
    {
        Report(auditing, new ExperienceGrantAccessFailure(accesses, auditing.Mode, failure));
        return auditing.Mode != ExperienceGrantAuditingMode.Required;
    }

    /// <summary>
    /// Reports missing access rows to the host. A throwing callback is swallowed: the host's own
    /// logging must not change what the read does, in either mode.
    /// </summary>
    private static void Report(ExperienceGrantAuditing auditing, ExperienceGrantAccessFailure failure)
    {
        try
        {
            auditing.OnNotRecorded(failure);
        }
        catch
        {
            // Intentionally swallowed, exactly as the grant-support notice is.
        }
    }
}
