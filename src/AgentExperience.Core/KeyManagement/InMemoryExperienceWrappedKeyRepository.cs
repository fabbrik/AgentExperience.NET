using AgentExperience.Abstractions;

namespace AgentExperience.Core.KeyManagement;

/// <summary>
/// An <see cref="IExperienceWrappedKeyRepository"/> held in this process's memory. For tests and local
/// development only.
/// </summary>
/// <remarks>
/// <b>Not for production.</b> A process restart loses every key, which makes every sealed record
/// permanently unreadable -- a crypto-shredding of the whole store. A production repository is durable,
/// lives outside the database's backup domain, and has a backup retention the host has chosen as its
/// erasure window. Thread-safe.
/// </remarks>
public sealed class InMemoryExperienceWrappedKeyRepository : IExperienceWrappedKeyRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<ExperienceKeyReference, ExperienceWrappedKey?> _entries = [];

    /// <inheritdoc />
    public ValueTask<ExperienceWrappedKeyEntry?> GetAsync(ExperienceKeyReference reference, CancellationToken cancellationToken)
    {
        KeyReferenceEncoding.Validate(reference);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_entries.TryGetValue(reference, out var wrapped)
                ? new ExperienceWrappedKeyEntry(reference, wrapped)
                : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<ExperienceWrappedKeyEntry> AddIfAbsentAsync(
        ExperienceKeyReference reference,
        ExperienceWrappedKey wrappedKey,
        CancellationToken cancellationToken)
    {
        KeyReferenceEncoding.Validate(reference);
        ArgumentNullException.ThrowIfNull(wrappedKey);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_entries.TryGetValue(reference, out var stored))
            {
                stored = Copy(wrappedKey);
                _entries[reference] = stored;
            }

            return ValueTask.FromResult(new ExperienceWrappedKeyEntry(reference, stored));
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> ReplaceAsync(
        ExperienceKeyReference reference,
        ExperienceWrappedKey expected,
        ExperienceWrappedKey replacement,
        CancellationToken cancellationToken)
    {
        KeyReferenceEncoding.Validate(reference);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(replacement);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_entries.TryGetValue(reference, out var stored) || stored is null || !SameWrap(stored, expected))
            {
                return ValueTask.FromResult(false);
            }

            _entries[reference] = Copy(replacement);
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask DestroyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken)
    {
        KeyReferenceEncoding.Validate(reference);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            // A marker with no key material: the reference can never be given a key again.
            _entries[reference] = null;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ExperienceWrappedKeyEntry>> ListNotWrappedUnderAsync(
        string currentKeyEncryptionKeyId,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentKeyEncryptionKeyId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<ExperienceWrappedKeyEntry> page = _entries
                .Where(entry => entry.Value is { } wrapped
                    && !string.Equals(wrapped.KeyEncryptionKeyId, currentKeyEncryptionKeyId, StringComparison.Ordinal))
                .Take(limit)
                .Select(entry => new ExperienceWrappedKeyEntry(entry.Key, entry.Value))
                .ToArray();
            return ValueTask.FromResult(page);
        }
    }

    private static ExperienceWrappedKey Copy(ExperienceWrappedKey wrapped) =>
        new(wrapped.KeyEncryptionKeyId, wrapped.Ciphertext.ToArray());

    private static bool SameWrap(ExperienceWrappedKey left, ExperienceWrappedKey right) =>
        string.Equals(left.KeyEncryptionKeyId, right.KeyEncryptionKeyId, StringComparison.Ordinal)
        && left.Ciphertext.Span.SequenceEqual(right.Ciphertext.Span);
}
