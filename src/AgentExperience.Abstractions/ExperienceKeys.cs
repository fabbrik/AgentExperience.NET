using System.Security.Cryptography;

namespace AgentExperience.Abstractions;

/// <summary>
/// Names the one Experience Record a data key belongs to: its ID and its own stored (owner) scope. Every
/// operation on <see cref="IExperienceKeyStore"/> is scoped to exactly one of these, so destroying one
/// record's key can never reach another record's.
/// </summary>
/// <remarks>
/// The scope is part of the reference, not a hint: a key created for <c>(id, scope A)</c> is never returned
/// for <c>(id, scope B)</c>. <see cref="Scope"/> is a record, so two references are equal exactly when the
/// ID and all six scope fields are equal, compared ordinally.
/// </remarks>
/// <param name="ExperienceId">The record's ID. Must not be <see cref="Guid.Empty"/>.</param>
/// <param name="Scope">The scope the record is stored under -- the owner's, never a grant recipient's.</param>
public sealed record ExperienceKeyReference(Guid ExperienceId, Scope Scope);

/// <summary>What a key store knows about one <see cref="ExperienceKeyReference"/>.</summary>
public enum ExperienceKeyStatus
{
    /// <summary>The key exists and was returned.</summary>
    Active = 0,

    /// <summary>
    /// The key was destroyed. Permanent: a destroyed reference is never given a key again, so every
    /// ciphertext ever sealed under it, wherever a copy of it lives, stays unreadable.
    /// </summary>
    Destroyed = 1,

    /// <summary>
    /// The store has never held a key for this reference. For a record that has sealed data this is a
    /// configuration failure (the wrong or an empty key store), never "erased".
    /// </summary>
    NotFound = 2,
}

/// <summary>
/// A 256-bit data key, handed out by an <see cref="IExperienceKeyStore"/> for one record. The bytes are
/// zeroed when it is disposed; a caller disposes it as soon as it has sealed or opened what it needed.
/// </summary>
public sealed class ExperienceDataKey : IDisposable
{
    /// <summary>The size of every data key, in bytes: AES-256.</summary>
    public const int SizeInBytes = 32;

    private readonly byte[] _key;
    private bool _disposed;

    /// <summary>Copies <paramref name="key"/>, which must be exactly <see cref="SizeInBytes"/> bytes.</summary>
    /// <param name="key">The key material. The caller keeps ownership of (and should zero) its own copy.</param>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not <see cref="SizeInBytes"/> bytes.</exception>
    public ExperienceDataKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != SizeInBytes)
        {
            throw new ArgumentException($"A data key is exactly {SizeInBytes} bytes.", nameof(key));
        }

        _key = key.ToArray();
    }

    /// <summary>The key material. Valid until the key is disposed.</summary>
    /// <exception cref="ObjectDisposedException">The key has been disposed.</exception>
    public ReadOnlySpan<byte> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _key;
        }
    }

    /// <summary>Zeroes the key material.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            CryptographicOperations.ZeroMemory(_key);
            _disposed = true;
        }
    }
}

/// <summary>
/// The answer to <see cref="IExperienceKeyStore.CreateKeyAsync"/> or
/// <see cref="IExperienceKeyStore.GetKeyAsync"/>: a status, and the key exactly when it is
/// <see cref="ExperienceKeyStatus.Active"/>. The caller owns and disposes <see cref="Key"/>.
/// </summary>
/// <param name="Status">What the store knows about the reference.</param>
/// <param name="Key">The data key when <paramref name="Status"/> is <see cref="ExperienceKeyStatus.Active"/>; otherwise <see langword="null"/>.</param>
public readonly record struct ExperienceKeyLookup(ExperienceKeyStatus Status, ExperienceDataKey? Key)
{
    /// <summary>An active key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>A lookup carrying <paramref name="key"/>.</returns>
    public static ExperienceKeyLookup Active(ExperienceDataKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new(ExperienceKeyStatus.Active, key);
    }

    /// <summary>The reference's key was destroyed.</summary>
    public static ExperienceKeyLookup Destroyed { get; } = new(ExperienceKeyStatus.Destroyed, null);

    /// <summary>The store never held a key for the reference.</summary>
    public static ExperienceKeyLookup NotFound { get; } = new(ExperienceKeyStatus.NotFound, null);
}

/// <summary>
/// Custody of the per-record data keys crypto-shredding seals Experience Records with. Erasing a record
/// destroys its key, and that is what makes every copy of the record's ciphertext -- in backups, replicas,
/// WAL, dead tuples -- unreadable.
/// </summary>
/// <remarks>
/// <para>
/// <b>The property is only as true as the key store's custody.</b> A key store must keep its keys
/// <em>outside the database's backup domain</em>: not in the same database, not in the same backups, not
/// on a replica of it. A key that is restored alongside the ciphertext it protects protects nothing. And
/// the key store's <em>own</em> backups bound the erasure: a destroyed key survives in every key-store
/// backup taken before the destruction, so the retention of those backups is how long an erased record can
/// still be recovered by someone holding both. See the store README's key custody section.
/// </para>
/// <para>
/// <b>Obligations the type system cannot express.</b>
/// <list type="bullet">
/// <item><see cref="CreateKeyAsync"/> is get-or-create and atomic: two concurrent calls for one reference
/// return the same key, never two different ones.</item>
/// <item><see cref="DestroyKeyAsync"/> is idempotent and permanent. Afterwards
/// <see cref="CreateKeyAsync"/> and <see cref="GetKeyAsync"/> for that reference answer
/// <see cref="ExperienceKeyStatus.Destroyed"/> for ever -- never a fresh key. A store that forgot a
/// destruction and minted a new key would make a half-erased record look live again.</item>
/// <item><see cref="DestroyKeyAsync"/> returns only once the key is gone from every place this store will
/// ever read it from again. If it cannot promise that, it throws: the erasure then rolls back and the record
/// stays live, which is the safe direction.</item>
/// <item>A reference is scoped: the key for <c>(id, scope A)</c> is never returned for
/// <c>(id, scope B)</c>.</item>
/// </list>
/// </para>
/// <para>
/// <c>AgentExperience.Core</c> ships <c>EnvelopeExperienceKeyStore</c>, which keeps each data key wrapped by
/// a host key-encryption key (a KMS key) and lets a host plug any KMS through
/// <see cref="IExperienceKeyEncryptionKey"/> and <see cref="IExperienceWrappedKeyRepository"/>.
/// </para>
/// </remarks>
public interface IExperienceKeyStore
{
    /// <summary>
    /// Returns the record's key, creating it first when the record has none. Returns
    /// <see cref="ExperienceKeyStatus.Destroyed"/> -- and creates nothing -- when the reference's key was
    /// destroyed.
    /// </summary>
    /// <param name="reference">The record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see cref="ExperienceKeyStatus.Active"/> with the key, or <see cref="ExperienceKeyStatus.Destroyed"/>.</returns>
    ValueTask<ExperienceKeyLookup> CreateKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken);

    /// <summary>Returns the record's key without creating one.</summary>
    /// <param name="reference">The record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see cref="ExperienceKeyStatus.Active"/> with the key, <see cref="ExperienceKeyStatus.Destroyed"/>, or <see cref="ExperienceKeyStatus.NotFound"/>.</returns>
    ValueTask<ExperienceKeyLookup> GetKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken);

    /// <summary>
    /// Destroys the record's key, permanently. Idempotent: destroying a destroyed key, or a reference that
    /// never had one, succeeds and leaves the reference <see cref="ExperienceKeyStatus.Destroyed"/>.
    /// </summary>
    /// <param name="reference">The record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes once the key is gone.</returns>
    ValueTask DestroyKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken);
}

/// <summary>
/// A data key wrapped (encrypted) by a host key-encryption key.
/// </summary>
/// <param name="KeyEncryptionKeyId">Which key-encryption key, and which version of it, wrapped the data key. Never blank.</param>
/// <param name="Ciphertext">The wrapped data key, in whatever format the key-encryption key produces.</param>
public sealed record ExperienceWrappedKey(string KeyEncryptionKeyId, ReadOnlyMemory<byte> Ciphertext);

/// <summary>
/// The host's key-encryption key (KEK): the key that wraps every per-record data key. Implement this over a
/// KMS -- Azure Key Vault <c>wrapKey</c>/<c>unwrapKey</c>, AWS KMS <c>Encrypt</c>/<c>Decrypt</c> with an
/// encryption context, HashiCorp Vault transit -- so the KEK never leaves it.
/// </summary>
/// <remarks>
/// <para>
/// Both operations are bound to the <see cref="ExperienceKeyReference"/> (as AEAD associated data, or a
/// KMS encryption context): a wrapped key moved to another record's entry must fail to unwrap.
/// </para>
/// <para>
/// <b>Rotation.</b> <see cref="CurrentKeyId"/> names the KEK version new keys are wrapped under; older
/// versions must stay unwrappable until every entry has been re-wrapped (<c>EnvelopeExperienceKeyStore.RewrapAsync</c>).
/// Retiring an old KEK version after that is also what bounds the key store's own backups: a key-store
/// backup wrapped under a retired KEK cannot resurrect a destroyed data key.
/// </para>
/// </remarks>
public interface IExperienceKeyEncryptionKey
{
    /// <summary>The ID of the key-encryption key new data keys are wrapped under. Never blank.</summary>
    string CurrentKeyId { get; }

    /// <summary>Wraps <paramref name="dataKey"/> under <see cref="CurrentKeyId"/>.</summary>
    /// <param name="dataKey">The data key to wrap.</param>
    /// <param name="reference">The record the key belongs to, bound into the wrap.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The wrapped key, naming the KEK that wrapped it.</returns>
    ValueTask<ExperienceWrappedKey> WrapAsync(ReadOnlyMemory<byte> dataKey, ExperienceKeyReference reference, CancellationToken cancellationToken);

    /// <summary>
    /// Unwraps <paramref name="wrappedKey"/>. Throws when it cannot: an unknown or retired KEK, a wrap bound
    /// to another reference, or a tampered ciphertext.
    /// </summary>
    /// <param name="wrappedKey">The wrapped key.</param>
    /// <param name="reference">The record the key belongs to; must be the one it was wrapped for.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The data key. The caller zeroes it.</returns>
    ValueTask<byte[]> UnwrapAsync(ExperienceWrappedKey wrappedKey, ExperienceKeyReference reference, CancellationToken cancellationToken);
}

/// <summary>
/// One entry of an <see cref="IExperienceWrappedKeyRepository"/>: a reference and its wrapped key, or a
/// destroyed marker (<see cref="WrappedKey"/> <see langword="null"/>) that carries no key material.
/// </summary>
/// <param name="Reference">The record.</param>
/// <param name="WrappedKey">The wrapped key, or <see langword="null"/> when it was destroyed.</param>
public sealed record ExperienceWrappedKeyEntry(ExperienceKeyReference Reference, ExperienceWrappedKey? WrappedKey)
{
    /// <summary>Whether this entry is a destroyed marker.</summary>
    public bool IsDestroyed => WrappedKey is null;
}

/// <summary>
/// Durable storage for wrapped data keys, which <c>EnvelopeExperienceKeyStore</c> uses. This is where the
/// crypto-shredding property lives or dies: it must be <b>outside the database's backup domain</b> -- a
/// different database, a secrets store, a blob container -- and its own backups must be retained no longer
/// than the host is prepared to let an erased record stay recoverable.
/// </summary>
/// <remarks>
/// Every operation is atomic per reference. A destroyed entry is kept as a marker with no key material, so
/// <see cref="AddIfAbsentAsync"/> can refuse to re-create it.
/// </remarks>
public interface IExperienceWrappedKeyRepository
{
    /// <summary>The stored entry, or <see langword="null"/> when there has never been one.</summary>
    /// <param name="reference">The record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The entry, which may be a destroyed marker, or <see langword="null"/>.</returns>
    ValueTask<ExperienceWrappedKeyEntry?> GetAsync(ExperienceKeyReference reference, CancellationToken cancellationToken);

    /// <summary>
    /// Stores <paramref name="wrappedKey"/> when the reference has no entry, atomically, and returns
    /// whatever entry the reference has afterwards: the one just stored, one a concurrent caller stored
    /// first, or a destroyed marker.
    /// </summary>
    /// <param name="reference">The record.</param>
    /// <param name="wrappedKey">The candidate wrapped key.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The entry that won.</returns>
    ValueTask<ExperienceWrappedKeyEntry> AddIfAbsentAsync(ExperienceKeyReference reference, ExperienceWrappedKey wrappedKey, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the wrapped key with <paramref name="replacement"/> only while the stored one is still
    /// <paramref name="expected"/> (compared by KEK ID and ciphertext bytes). Used to re-wrap under a new KEK.
    /// </summary>
    /// <param name="reference">The record.</param>
    /// <param name="expected">The wrapped key the caller read.</param>
    /// <param name="replacement">The same data key, wrapped under another KEK.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="true"/> when replaced; <see langword="false"/> when the entry changed or was destroyed meanwhile.</returns>
    ValueTask<bool> ReplaceAsync(ExperienceKeyReference reference, ExperienceWrappedKey expected, ExperienceWrappedKey replacement, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the wrapped key and leaves a destroyed marker, permanently. Idempotent. Returns only once the
    /// wrapped key is gone from every place this repository reads from.
    /// </summary>
    /// <param name="reference">The record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes once the wrapped key is gone.</returns>
    ValueTask DestroyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken);

    /// <summary>
    /// Up to <paramref name="limit"/> live entries whose wrapped key is <em>not</em> under
    /// <paramref name="currentKeyEncryptionKeyId"/>, for re-wrapping. An entry re-wrapped since drops out, so
    /// calling this again after re-wrapping a page makes progress without a cursor.
    /// </summary>
    /// <param name="currentKeyEncryptionKeyId">The KEK every entry should end up under.</param>
    /// <param name="limit">The most entries to return; at least 1.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The entries, none of them destroyed markers.</returns>
    ValueTask<IReadOnlyList<ExperienceWrappedKeyEntry>> ListNotWrappedUnderAsync(string currentKeyEncryptionKeyId, int limit, CancellationToken cancellationToken);
}
