using AgentExperience.Abstractions;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// Structural validation run before authorization and before any database access. Collects every
/// error (never stops at the first) with a field path and a content-free message. Rejects nulls in
/// non-nullable members so a stored payload is always readable back.
/// </summary>
/// <remarks>
/// This part holds the rules only this adapter has: erasure, retention, sealing, grants, and the vector index.
/// The rules for the core storage ports (records, lifecycle events, history, supersession, queries, candidate
/// searches and reuse feedback) are in <c>src/Shared/ExperienceRecordValidator.Shared.cs</c>, which
/// <c>AgentExperience.Storage.InMemory</c> links too, so both stores refuse exactly the same requests.
/// </remarks>
internal static partial class ExperienceRecordValidator
{
    /// <summary>
    /// Validates an erasure: the scope, the record, and the optional expected revision. A negative
    /// revision is refused rather than treated as "any", because a caller that computed one is asking
    /// for something it did not mean -- and the thing it is asking for here is destructive.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateDelete(Scope scope, Guid experienceId, long? expectedRevision)
    {
        var errors = new List<StoreValidationError>();
        if (experienceId == Guid.Empty)
        {
            errors.Add(new("ExperienceId", "must not be an empty GUID."));
        }

        if (expectedRevision is < 0)
        {
            errors.Add(new("ExpectedRevision", "must not be negative."));
        }

        ValidateScope(scope, "Scope", errors);
        return errors;
    }

    /// <summary>
    /// Validates a retention sweep: the scope, the age, and the batch bound. A non-positive age is
    /// refused rather than read as "delete everything": retention is indefinite until a host names a
    /// span, and a zero or negative one is the shape a misconfigured setting takes.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateRetentionSweep(Scope scope, TimeSpan retentionAge, int batchSize) =>
        ValidateRetentionSweep(scope, retentionAge, batchSize, ScopeMatch.Exact);

    /// <summary>
    /// The same, with the scope match. A value <see cref="ScopeMatch"/> does not define is refused rather
    /// than read as either member: a destructive operation never guesses how wide it was asked to reach.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateRetentionSweep(Scope scope, TimeSpan retentionAge, int batchSize, ScopeMatch match)
    {
        var errors = new List<StoreValidationError>();
        ValidateScopeMatch(match, errors);

        if (retentionAge <= TimeSpan.Zero)
        {
            errors.Add(new("RetentionAge", "must be strictly positive; there is no retention age that means 'delete everything'."));
        }

        if (batchSize is < PostgresExperienceRecordStore.MinSweepBatchSize or > PostgresExperienceRecordStore.MaxSweepBatchSize)
        {
            errors.Add(new(
                "BatchSize",
                $"must be between {PostgresExperienceRecordStore.MinSweepBatchSize} and {PostgresExperienceRecordStore.MaxSweepBatchSize}."));
        }

        ValidateScope(scope, "Scope", errors);
        return errors;
    }

    /// <summary>
    /// Validates one batch of the crypto-shredding upgrade: the scope, the scope match, the batch bound, and
    /// that the store has an <see cref="ExperienceEncryption"/> to seal with at all.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateSealing(Scope scope, int batchSize, ScopeMatch match, bool encryptionConfigured)
    {
        var errors = new List<StoreValidationError>();
        ValidateScopeMatch(match, errors);

        if (!encryptionConfigured)
        {
            errors.Add(new("Encryption", "this store was constructed without an ExperienceEncryption, so there is no key store to seal with."));
        }

        if (batchSize is < PostgresExperienceRecordStore.MinSweepBatchSize or > PostgresExperienceRecordStore.MaxSweepBatchSize)
        {
            errors.Add(new(
                "BatchSize",
                $"must be between {PostgresExperienceRecordStore.MinSweepBatchSize} and {PostgresExperienceRecordStore.MaxSweepBatchSize}."));
        }

        ValidateScope(scope, "Scope", errors);
        return errors;
    }

    /// <summary>
    /// Validates an authorship backfill (story 17.1): the scope, the scope match, the batch bound, and that the store
    /// has the key store it needs to open a sealed record.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateAuthorshipBackfill(Scope scope, int batchSize, ScopeMatch match, bool encryptionConfigured)
    {
        var errors = new List<StoreValidationError>();
        ValidateScopeMatch(match, errors);

        if (!encryptionConfigured)
        {
            errors.Add(new("Encryption", "this store was constructed without an ExperienceEncryption, so there is no key store to open a sealed record with."));
        }

        if (batchSize is < PostgresExperienceRecordStore.MinSweepBatchSize or > PostgresExperienceRecordStore.MaxSweepBatchSize)
        {
            errors.Add(new(
                "BatchSize",
                $"must be between {PostgresExperienceRecordStore.MinSweepBatchSize} and {PostgresExperienceRecordStore.MaxSweepBatchSize}."));
        }

        ValidateScope(scope, "Scope", errors);
        return errors;
    }

    /// <summary>
    /// Validates an access-log purge: the owner scope, the scope match, the cutoff, and the batch bound.
    /// Whether the cutoff is old enough is the database's decision, on its own clock; here it only has
    /// to be set.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateGrantAccessPurge(Scope recordScope, DateTimeOffset cutoff, ScopeMatch match, int batchSize)
    {
        var errors = new List<StoreValidationError>();
        ValidateScopeMatch(match, errors);

        if (cutoff == default)
        {
            errors.Add(new("Cutoff", "must be set; there is no cutoff that means 'delete everything'."));
        }

        if (batchSize is < PostgresExperienceRecordStore.MinSweepBatchSize or > PostgresExperienceRecordStore.MaxSweepBatchSize)
        {
            errors.Add(new(
                "BatchSize",
                $"must be between {PostgresExperienceRecordStore.MinSweepBatchSize} and {PostgresExperienceRecordStore.MaxSweepBatchSize}."));
        }

        ValidateScope(recordScope, "RecordScope", errors);
        return errors;
    }

    private static void ValidateScopeMatch(ScopeMatch match, List<StoreValidationError> errors)
    {
        if (!Enum.IsDefined(match))
        {
            errors.Add(new("Match", $"must be {nameof(ScopeMatch.Exact)} or {nameof(ScopeMatch.Subtree)}."));
        }
    }

    /// <summary>
    /// Validates an expired-grant purge: the owner scope and the batch bound. There is no age
    /// parameter, because a grant carries its own: it is collected once its stored expiry has passed.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateGrantPurge(Scope recordScope, int batchSize)
    {
        var errors = new List<StoreValidationError>();

        if (batchSize is < PostgresExperienceRecordStore.MinSweepBatchSize or > PostgresExperienceRecordStore.MaxSweepBatchSize)
        {
            errors.Add(new(
                "BatchSize",
                $"must be between {PostgresExperienceRecordStore.MinSweepBatchSize} and {PostgresExperienceRecordStore.MaxSweepBatchSize}."));
        }

        ValidateScope(recordScope, "RecordScope", errors);
        return errors;
    }

    /// <summary>
    /// Validates a grant request: both scopes, the record it names, its reason, and -- the rule that
    /// makes a grant a grant rather than a scope change -- that the recipient keeps the record's
    /// tenant, application, and project. Each of those three is reported on its own field path, so a
    /// caller learns which boundary it tried to cross without being told anything about the record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <em>lower</em> bound on expiry is deliberately not checked against the local clock. Whether
    /// a grant is still live is decided by the database's clock in the read predicate, and rejecting
    /// an already-past expiry here would put a second, disagreeing clock in charge of the same
    /// question. The <c>experience_grants_expires_after_issue</c> constraint catches it against the
    /// clock that does decide.
    /// </para>
    /// <para>
    /// The <em>upper</em> bound is checked here, and it has to be: it is a host-configured interval,
    /// and a CHECK constraint cannot express one. A local clock is good enough for it because the
    /// bound is generous and one-sided -- skew of seconds cannot turn a reasonable window into an
    /// unreasonable one -- and because the database keeps its own fixed ceiling underneath
    /// (<c>experience_grants_lifetime_bounded</c>), which no clock of ours is involved in.
    /// </para>
    /// </remarks>
    /// <param name="request">The grant to validate.</param>
    /// <param name="policy">The bounds this adapter administers grants under.</param>
    /// <param name="now">The moment to measure the maximum lifetime from.</param>
    public static IReadOnlyList<StoreValidationError> ValidateGrantRequest(
        ExperienceGrantRequest request,
        PostgresExperienceGrantPolicy policy,
        DateTimeOffset now)
    {
        var errors = new List<StoreValidationError>();

        if (request.GrantId == Guid.Empty)
        {
            errors.Add(new("GrantId", "must not be an empty GUID."));
        }

        if (request.ExperienceId == Guid.Empty)
        {
            errors.Add(new("ExperienceId", "must not be an empty GUID."));
        }

        ValidateScope(request.RecordScope, "RecordScope", errors);
        ValidateScope(request.RecipientScope, "RecipientScope", errors);
        RequireNotBlank(request.Reason, "Reason", errors);
        RefuseReservedText(request.Reason, "Reason", errors);

        if (request.ExpiresAt == default)
        {
            errors.Add(new("ExpiresAt", "must be set to when the grant stops permitting reads."));
        }
        else if (policy.ExceedsMaxLifetime(request.ExpiresAt, now))
        {
            // The message names the bound, not the offending value: a caller needs to know what it may
            // ask for, and echoing back what it asked for tells it nothing it did not already have.
            errors.Add(new(
                "ExpiresAt",
                $"must not be more than {policy.MaxLifetime} from now, which is the configured maximum grant lifetime."));
        }

        if (!Enum.IsDefined(request.Disclosure))
        {
            // An undefined level is never guessed into a defined one: stored, it could only be read
            // back as "not told", and issuing a grant whose disclosure nobody chose is not a grant.
            errors.Add(new("Disclosure", "must be LessonOnly, LessonAndApproach or LessonApproachAndArguments."));
        }
        else
        {
            ValidateGrantApproachArguments(request.Disclosure, request.ApproachArguments, errors);
        }

        if (request.RecordScope is { } record && request.RecipientScope is { } recipient)
        {
            RequireSameBound(record.TenantId, recipient.TenantId, "RecipientScope.TenantId", errors);
            RequireSameBound(record.ApplicationId, recipient.ApplicationId, "RecipientScope.ApplicationId", errors);
            RequireSameBound(record.ProjectId, recipient.ProjectId, "RecipientScope.ProjectId", errors);

            if (record == recipient)
            {
                // A grant to the scope that already owns the record permits nothing, and storing one
                // would leave an audit row claiming access was given when none was.
                errors.Add(new("RecipientScope", "must differ from the record's own scope, which already permits the read."));
            }
        }

        return errors;
    }

    /// <summary>
    /// The characters an argument key may not contain, because the <c>Approach:</c> line uses them to delimit an
    /// argument. The same set the MAF adapter refuses in <c>ExperienceInjectionOptions.ApproachArguments</c>: a key
    /// the owner could store but no reader could configure would be consent to nothing.
    /// </summary>
    internal const string ForbiddenApproachArgumentKeyCharacters = "=(),\"\\";

    /// <summary>
    /// The owner's argument allowlist on a grant request: present exactly under
    /// <see cref="ExperienceGrantDisclosure.LessonApproachAndArguments"/>, non-empty, bounded, and made only of keys
    /// a reader could also allowlist. Every error is reported on <c>ApproachArguments</c> and names the rule, never
    /// the offending text.
    /// </summary>
    private static void ValidateGrantApproachArguments(
        ExperienceGrantDisclosure disclosure,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? allowlist,
        List<StoreValidationError> errors)
    {
        const string Path = "ApproachArguments";

        if (disclosure != ExperienceGrantDisclosure.LessonApproachAndArguments)
        {
            if (allowlist is not null)
            {
                // Keys under a level that shows no argument value would be stored consent to nothing, and would
                // read back as though the owner had agreed to show them.
                errors.Add(new(Path, "must be null unless Disclosure is LessonApproachAndArguments."));
            }

            return;
        }

        if (allowlist is null || allowlist.Count == 0)
        {
            errors.Add(new(Path, "must name at least one tool and argument key when Disclosure is LessonApproachAndArguments."));
            return;
        }

        if (allowlist.Count > ExperienceGrant.MaxApproachArgumentTools)
        {
            errors.Add(new(Path, $"must name at most {ExperienceGrant.MaxApproachArgumentTools} tools."));
            return;
        }

        // Tool names are compared ordinally whatever comparer the caller's dictionary used: stored as JSON object
        // keys, two names that differ only by case are two tools.
        var tools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (toolName, keys) in allowlist)
        {
            if (string.IsNullOrWhiteSpace(toolName)
                || toolName.Length > ExperienceGrant.MaxApproachArgumentToolNameLength
                || toolName.Any(char.IsControl)
                || !tools.Add(toolName))
            {
                errors.Add(new(Path, $"names a tool that is blank, longer than {ExperienceGrant.MaxApproachArgumentToolNameLength} characters, contains a control character, or is named twice."));
                return;
            }

            if (keys is null || keys.Count == 0 || keys.Count > ExperienceGrant.MaxApproachArgumentKeysPerTool)
            {
                errors.Add(new(Path, $"must list between 1 and {ExperienceGrant.MaxApproachArgumentKeysPerTool} argument keys for every tool it names."));
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                if (string.IsNullOrEmpty(key)
                    || key.Length > ExperienceGrant.MaxApproachArgumentKeyLength
                    || key.Any(IsForbiddenApproachArgumentKeyCharacter)
                    || !seen.Add(key))
                {
                    errors.Add(new(Path, $"holds an argument key that is blank, longer than {ExperienceGrant.MaxApproachArgumentKeyLength} characters, contains whitespace, a control, format or surrogate character or one of {ForbiddenApproachArgumentKeyCharacters}, or is listed twice for one tool."));
                    return;
                }
            }
        }
    }

    private static bool IsForbiddenApproachArgumentKeyCharacter(char character) =>
        char.IsWhiteSpace(character)
        || char.IsControl(character)
        || char.IsSurrogate(character)
        || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format
        || ForbiddenApproachArgumentKeyCharacters.Contains(character, StringComparison.Ordinal);

    public static IReadOnlyList<StoreValidationError> ValidateGrantRevocation(ExperienceGrantRevocation revocation)
    {
        var errors = new List<StoreValidationError>();

        if (revocation.GrantId == Guid.Empty)
        {
            errors.Add(new("GrantId", "must not be an empty GUID."));
        }

        ValidateScope(revocation.RecordScope, "RecordScope", errors);
        RequireNotBlank(revocation.Reason, "Reason", errors);
        RefuseReservedText(revocation.Reason, "Reason", errors);

        return errors;
    }

    public static IReadOnlyList<StoreValidationError> ValidateGrantList(Scope recordScope, Guid experienceId, int limit)
    {
        var errors = new List<StoreValidationError>();

        if (experienceId == Guid.Empty)
        {
            errors.Add(new("ExperienceId", "must not be an empty GUID."));
        }

        if (limit is < ExperienceGrant.MinListLimit or > ExperienceGrant.MaxListLimit)
        {
            errors.Add(new(
                "Limit",
                $"must be between {ExperienceGrant.MinListLimit} and {ExperienceGrant.MaxListLimit}."));
        }

        ValidateScope(recordScope, "RecordScope", errors);
        return errors;
    }

    /// <summary>
    /// Validates a query over the grant access trail: the owner scope it reads within, the optional
    /// single record it narrows to, and the page bound.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateGrantAccessQuery(ExperienceGrantAccessQuery query)
    {
        var errors = new List<StoreValidationError>();

        if (query.ExperienceId == Guid.Empty)
        {
            // Null narrows to nothing and is the "everything this scope disclosed" question; an empty
            // GUID is a caller that meant to name a record and did not.
            errors.Add(new("ExperienceId", "must not be an empty GUID; use null to read the whole scope."));
        }

        if (query.Limit is < ExperienceGrantAccessQuery.MinLimit or > ExperienceGrantAccessQuery.MaxLimit)
        {
            errors.Add(new(
                "Limit",
                $"must be between {ExperienceGrantAccessQuery.MinLimit} and {ExperienceGrantAccessQuery.MaxLimit}."));
        }

        if (query.StartAfter is { } cursor && cursor.AccessId == Guid.Empty)
        {
            errors.Add(new("StartAfter.AccessId", "must not be an empty GUID."));
        }

        ValidateScope(query.RecordScope, "RecordScope", errors);
        return errors;
    }

    public static IReadOnlyList<StoreValidationError> ValidateGrantHistory(Scope recordScope, Guid grantId)
    {
        var errors = new List<StoreValidationError>();

        if (grantId == Guid.Empty)
        {
            errors.Add(new("GrantId", "must not be an empty GUID."));
        }

        ValidateScope(recordScope, "RecordScope", errors);
        return errors;
    }

    /// <summary>
    /// Validates the explicit administrator authority itself. It is validated, not merely checked for
    /// presence, because its <see cref="GrantAdministration.AuthorizedAt"/> is recorded on the audit
    /// event: an unset value would put "authority established at year zero" into the trail.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateAdministration(GrantAdministration administration)
    {
        var errors = new List<StoreValidationError>();
        RequireNotBlank(administration.AdministratorPrincipalId, "Administration.AdministratorPrincipalId", errors);

        if (administration.AuthorizedAt == default)
        {
            errors.Add(new("Administration.AuthorizedAt", "must be set to when the host established this authority."));
        }

        return errors;
    }

    private static void RequireSameBound(string? recordValue, string? recipientValue, string path, List<StoreValidationError> errors)
    {
        if (!string.Equals(recordValue, recipientValue, StringComparison.Ordinal))
        {
            errors.Add(new(path, "must equal the record's, because a grant may relax only the team, agent, and user fields."));
        }
    }
    /// <summary>
    /// Validates a conditional index write: the scope the record must lie in, the record ID, the
    /// descriptor that makes the write conditional, and the vector itself. The vector's length is
    /// checked against the descriptor's dimension here, so the two can never be stored disagreeing.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateIndexWrite(ExperienceIndexWrite write)
    {
        var errors = new List<StoreValidationError>();
        ValidateScope(write.Scope, "Scope", errors);

        if (write.ExperienceId == Guid.Empty)
        {
            errors.Add(new("ExperienceId", "must not be an empty GUID."));
        }

        if (write.Descriptor is null)
        {
            errors.Add(new("Descriptor", Required));
            return errors;
        }

        ValidateDescriptor(write.Descriptor, "Descriptor", errors);
        ValidateVector(write.Vector, "Vector", errors);

        if (write.Descriptor.Dimension > 0 && write.Vector.Length != write.Descriptor.Dimension)
        {
            errors.Add(new("Vector", "must hold exactly Descriptor.Dimension components."));
        }

        return errors;
    }

    /// <summary>Validates a vector removal: the scope the embedding must lie in, and the record it belongs to.</summary>
    public static IReadOnlyList<StoreValidationError> ValidateIndexRemove(Scope scope, Guid experienceId)
    {
        var errors = new List<StoreValidationError>();

        if (experienceId == Guid.Empty)
        {
            errors.Add(new("ExperienceId", "must not be an empty GUID."));
        }

        ValidateScope(scope, "Scope", errors);
        return errors;
    }

    /// <summary>Validates a scoped index scan: the scope, the model whose descriptors to report, the optional ID filter, and the bound.</summary>
    public static IReadOnlyList<StoreValidationError> ValidateIndexScan(ExperienceIndexScan scan)
    {
        var errors = new List<StoreValidationError>();
        ValidateScope(scan.Scope, "Scope", errors);
        RequireNotBlank(scan.ModelId, "ModelId", errors);

        if (scan.ModelId is { Length: > ExperienceEmbeddingDescriptor.MaxModelIdLength })
        {
            errors.Add(new("ModelId", $"must be at most {ExperienceEmbeddingDescriptor.MaxModelIdLength} characters."));
        }

        if (scan.EligibleStatuses is null)
        {
            errors.Add(new("EligibleStatuses", Required));
        }
        else if (scan.EligibleStatuses.Count == 0)
        {
            errors.Add(new("EligibleStatuses", "must contain at least one status."));
        }
        else
        {
            for (var i = 0; i < scan.EligibleStatuses.Count; i++)
            {
                RequireDefined(scan.EligibleStatuses[i], $"EligibleStatuses[{i}]", errors);
            }
        }

        RequireUnitInterval(scan.MinimumConfidence, "MinimumConfidence", errors);

        if (scan.StartAfterId == Guid.Empty)
        {
            errors.Add(new("StartAfterId", "must be null or a non-empty GUID."));
        }

        if (scan.ExperienceIds is not null)
        {
            if (scan.ExperienceIds.Count == 0)
            {
                errors.Add(new("ExperienceIds", "must contain at least one identifier when supplied."));
            }
            else if (scan.ExperienceIds.Count > ExperienceIndexScan.MaxLimit)
            {
                errors.Add(new("ExperienceIds", $"must hold at most {ExperienceIndexScan.MaxLimit} identifiers."));
            }
            else
            {
                for (var i = 0; i < scan.ExperienceIds.Count; i++)
                {
                    if (scan.ExperienceIds[i] == Guid.Empty)
                    {
                        errors.Add(new($"ExperienceIds[{i}]", "must not be an empty GUID."));
                    }
                }
            }
        }

        if (scan.Limit is < ExperienceIndexScan.MinLimit or > ExperienceIndexScan.MaxLimit)
        {
            errors.Add(new("Limit", $"must be between {ExperienceIndexScan.MinLimit} and {ExperienceIndexScan.MaxLimit}."));
        }

        return errors;
    }

    /// <summary>
    /// Validates a scoped vector search. The field paths and the bounds deliberately mirror
    /// <see cref="ValidateCandidateQuery"/>, so both retrieval channels reject the same requests for
    /// the same reasons.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateVectorQuery(ExperienceVectorQuery query)
    {
        var errors = new List<StoreValidationError>();
        ValidateScope(query.Scope, "Scope", errors);
        RequireNotBlank(query.ModelId, "ModelId", errors);

        if (query.ModelId is { Length: > ExperienceEmbeddingDescriptor.MaxModelIdLength })
        {
            errors.Add(new("ModelId", $"must be at most {ExperienceEmbeddingDescriptor.MaxModelIdLength} characters."));
        }

        ValidateVector(query.Vector, "Vector", errors);

        if (query.EligibleStatuses is null)
        {
            errors.Add(new("EligibleStatuses", Required));
        }
        else if (query.EligibleStatuses.Count == 0)
        {
            errors.Add(new("EligibleStatuses", "must contain at least one status."));
        }
        else
        {
            for (var i = 0; i < query.EligibleStatuses.Count; i++)
            {
                RequireDefined(query.EligibleStatuses[i], $"EligibleStatuses[{i}]", errors);
            }
        }

        RequireUnitInterval(query.MinimumConfidence, "MinimumConfidence", errors);

        if (query.Limit is < ExperienceCandidateQuery.MinLimit or > ExperienceCandidateQuery.MaxLimit)
        {
            errors.Add(new("Limit", $"must be between {ExperienceCandidateQuery.MinLimit} and {ExperienceCandidateQuery.MaxLimit}."));
        }

        return errors;
    }

    private static void ValidateDescriptor(ExperienceEmbeddingDescriptor descriptor, string path, List<StoreValidationError> errors)
    {
        RequireNotBlank(descriptor.ModelId, $"{path}.ModelId", errors);
        if (descriptor.ModelId is { Length: > ExperienceEmbeddingDescriptor.MaxModelIdLength })
        {
            errors.Add(new($"{path}.ModelId", $"must be at most {ExperienceEmbeddingDescriptor.MaxModelIdLength} characters."));
        }

        if (descriptor.Dimension is < 1 or > ExperienceEmbeddingDescriptor.MaxDimension)
        {
            errors.Add(new($"{path}.Dimension", $"must be between 1 and {ExperienceEmbeddingDescriptor.MaxDimension}."));
        }

        RequireNotBlank(descriptor.ContentHash, $"{path}.ContentHash", errors);

        if (descriptor.SourceRevision < 0)
        {
            errors.Add(new($"{path}.SourceRevision", "must not be negative."));
        }
    }

    /// <summary>
    /// A vector must be non-empty, within the dimension ceiling, and entirely finite. A NaN or an
    /// infinity would be accepted by pgvector's input parser in some forms and then poison every
    /// distance computed against it, so it is rejected before any database access.
    /// </summary>
    private static void ValidateVector(ReadOnlyMemory<float> vector, string path, List<StoreValidationError> errors)
    {
        if (vector.Length == 0)
        {
            errors.Add(new(path, "must hold at least one component."));
            return;
        }

        if (vector.Length > ExperienceEmbeddingDescriptor.MaxDimension)
        {
            errors.Add(new(path, $"must hold at most {ExperienceEmbeddingDescriptor.MaxDimension} components."));
            return;
        }

        foreach (var component in vector.Span)
        {
            if (!float.IsFinite(component))
            {
                errors.Add(new(path, "must hold only finite components."));
                return;
            }
        }
    }
}
