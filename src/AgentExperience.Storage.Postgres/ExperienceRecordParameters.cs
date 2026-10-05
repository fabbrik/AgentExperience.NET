using AgentExperience.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// The record store's parameter binding, including what other components share: the scope, the sealed search
/// texts and nullable values the SQL expects, and the scope match and timestamp truncation applied before binding.
/// </summary>
internal static class ExperienceRecordParameters
{
    /// <summary>
    /// Whether <paramref name="candidate"/> is <paramref name="root"/> itself (<see cref="ScopeMatch.Exact"/>),
    /// or at or beneath it (<see cref="ScopeMatch.Subtree"/>), exactly as <see cref="ScopeMatch"/> defines
    /// it: the three required fields equal, and each optional field of the root either null or equal.
    /// Ordinal throughout, like <see cref="AuthorizationContext.Permits"/>.
    /// </summary>
    internal static bool IsAtOrBeneath(Scope candidate, Scope root, ScopeMatch match)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(root);

        var required = string.Equals(candidate.TenantId, root.TenantId, StringComparison.Ordinal)
            && string.Equals(candidate.ApplicationId, root.ApplicationId, StringComparison.Ordinal)
            && string.Equals(candidate.ProjectId, root.ProjectId, StringComparison.Ordinal);

        return match switch
        {
            ScopeMatch.Exact => required
                && string.Equals(candidate.TeamId, root.TeamId, StringComparison.Ordinal)
                && string.Equals(candidate.AgentId, root.AgentId, StringComparison.Ordinal)
                && string.Equals(candidate.UserId, root.UserId, StringComparison.Ordinal),
            ScopeMatch.Subtree => required
                && (root.TeamId is null || string.Equals(candidate.TeamId, root.TeamId, StringComparison.Ordinal))
                && (root.AgentId is null || string.Equals(candidate.AgentId, root.AgentId, StringComparison.Ordinal))
                && (root.UserId is null || string.Equals(candidate.UserId, root.UserId, StringComparison.Ordinal)),
            _ => false,
        };
    }

    /// <summary>The three texts <see cref="ExperienceRecordSql.SealedSearchVectorExpression"/> analyses. Sent, never stored.</summary>
    internal static void AddSealedSearchParameters(NpgsqlParameterCollection parameters, string taskId, string? taskSummary, string? lesson)
    {
        parameters.Add(new NpgsqlParameter<string>("search_task_id", NpgsqlDbType.Text) { TypedValue = taskId });
        parameters.Add(NullableText("search_summary", taskSummary));
        parameters.Add(NullableText("search_lesson", lesson));
    }

    internal static void AddScopeParameters(NpgsqlParameterCollection parameters, Scope scope)
    {
        parameters.Add(new NpgsqlParameter<string>("tenant_id", NpgsqlDbType.Text) { TypedValue = scope.TenantId });
        parameters.Add(new NpgsqlParameter<string>("application_id", NpgsqlDbType.Text) { TypedValue = scope.ApplicationId });
        parameters.Add(new NpgsqlParameter<string>("project_id", NpgsqlDbType.Text) { TypedValue = scope.ProjectId });
        parameters.Add(NullableText("team_id", scope.TeamId));
        parameters.Add(NullableText("agent_id", scope.AgentId));
        parameters.Add(NullableText("user_id", scope.UserId));
    }

    internal static NpgsqlParameter NullableText(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = value is null ? DBNull.Value : value };

    internal static NpgsqlParameter NullableUuid(string name, Guid? value) =>
        new(name, NpgsqlDbType.Uuid) { Value = value is { } id ? id : DBNull.Value };

    internal static NpgsqlParameter NullableDouble(string name, double? value) =>
        new(name, NpgsqlDbType.Double) { Value = value is { } number ? number : DBNull.Value };

    internal static NpgsqlParameter NullableInt(string name, int? value) =>
        new(name, NpgsqlDbType.Integer) { Value = value is { } number ? number : DBNull.Value };

    internal static DateTimeOffset ToStoredTimestamp(DateTimeOffset value)
    {
        var utcTicks = value.UtcTicks;
        return new DateTimeOffset(utcTicks - (utcTicks % 10), TimeSpan.Zero);
    }
}
