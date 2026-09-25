using System.Security.Cryptography;
using AgentExperience.Abstractions;

namespace AgentExperience.Core.KeyManagement;

/// <summary>
/// A key-encryption key held <b>in this process</b>: AES-256-GCM over the data key, with the
/// <see cref="ExperienceKeyReference"/> and the KEK ID bound in as associated data. For tests and local
/// development only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not for production.</b> The KEK is only as safe as the process memory and configuration it came from,
/// and it never leaves the host's backup domain in any meaningful sense. A production deployment implements
/// <see cref="IExperienceKeyEncryptionKey"/> over a KMS instead, so the KEK never leaves it.
/// </para>
/// <para>
/// It holds several KEK versions so rotation can be exercised: <see cref="AddKey"/> a new version (optionally
/// making it current), re-wrap with <see cref="EnvelopeExperienceKeyStore.RewrapAsync"/>, then
/// <see cref="RetireKey"/> the old one. A wrapped key under a retired version can no longer be unwrapped by
/// anything, which is what bounds a key-store backup taken before the rotation.
/// </para>
/// <para>Thread-safe.</para>
/// </remarks>
public sealed class LocalExperienceKeyEncryptionKey : IExperienceKeyEncryptionKey
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    private string _currentKeyId;

    /// <summary>Creates a KEK with one version, which is current.</summary>
    /// <param name="keyId">The version's ID. Never blank.</param>
    /// <param name="key">32 bytes of key material. Copied.</param>
    public LocalExperienceKeyEncryptionKey(string keyId, ReadOnlySpan<byte> key)
    {
        ValidateKey(keyId, key);
        _keys[keyId] = key.ToArray();
        _currentKeyId = keyId;
    }

    /// <summary>Creates a KEK with one freshly generated version.</summary>
    /// <param name="keyId">The version's ID. Never blank.</param>
    /// <returns>The KEK.</returns>
    public static LocalExperienceKeyEncryptionKey Generate(string keyId)
    {
        Span<byte> key = stackalloc byte[KeySize];
        RandomNumberGenerator.Fill(key);
        try
        {
            return new LocalExperienceKeyEncryptionKey(keyId, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <inheritdoc />
    public string CurrentKeyId
    {
        get
        {
            lock (_gate)
            {
                return _currentKeyId;
            }
        }
    }

    /// <summary>Adds a KEK version.</summary>
    /// <param name="keyId">The new version's ID. Never blank, and not already present.</param>
    /// <param name="key">32 bytes of key material. Copied.</param>
    /// <param name="makeCurrent">Whether new data keys are wrapped under it from now on.</param>
    public void AddKey(string keyId, ReadOnlySpan<byte> key, bool makeCurrent)
    {
        ValidateKey(keyId, key);
        var copy = key.ToArray();
        lock (_gate)
        {
            if (!_keys.TryAdd(keyId, copy))
            {
                CryptographicOperations.ZeroMemory(copy);
                throw new ArgumentException($"A key-encryption key with ID '{keyId}' already exists.", nameof(keyId));
            }

            if (makeCurrent)
            {
                _currentKeyId = keyId;
            }
        }
    }

    /// <summary>
    /// Forgets a KEK version and zeroes it. Anything still wrapped under it can never be unwrapped again.
    /// The current version cannot be retired.
    /// </summary>
    /// <param name="keyId">The version to retire.</param>
    public void RetireKey(string keyId)
    {
        lock (_gate)
        {
            if (string.Equals(keyId, _currentKeyId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The current key-encryption key cannot be retired; make another one current first.");
            }

            if (_keys.Remove(keyId, out var key))
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<ExperienceWrappedKey> WrapAsync(ReadOnlyMemory<byte> dataKey, ExperienceKeyReference reference, CancellationToken cancellationToken)
    {
        KeyReferenceEncoding.Validate(reference);
        cancellationToken.ThrowIfCancellationRequested();
        if (dataKey.Length != ExperienceDataKey.SizeInBytes)
        {
            throw new ArgumentException($"A data key is exactly {ExperienceDataKey.SizeInBytes} bytes.", nameof(dataKey));
        }

        string keyId;
        byte[] kek;
        lock (_gate)
        {
            keyId = _currentKeyId;
            kek = _keys[keyId];

            var output = new byte[NonceSize + dataKey.Length + TagSize];
            var nonce = output.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(kek, TagSize);
            aes.Encrypt(
                nonce,
                dataKey.Span,
                output.AsSpan(NonceSize, dataKey.Length),
                output.AsSpan(NonceSize + dataKey.Length, TagSize),
                KeyReferenceEncoding.Encode(reference, keyId));
            return ValueTask.FromResult(new ExperienceWrappedKey(keyId, output));
        }
    }

    /// <inheritdoc />
    public ValueTask<byte[]> UnwrapAsync(ExperienceWrappedKey wrappedKey, ExperienceKeyReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wrappedKey);
        KeyReferenceEncoding.Validate(reference);
        cancellationToken.ThrowIfCancellationRequested();

        var wrapped = wrappedKey.Ciphertext.Span;
        if (wrapped.Length != NonceSize + ExperienceDataKey.SizeInBytes + TagSize)
        {
            throw new CryptographicException("The wrapped data key has the wrong length.");
        }

        lock (_gate)
        {
            if (!_keys.TryGetValue(wrappedKey.KeyEncryptionKeyId, out var kek))
            {
                throw new CryptographicException("The key-encryption key that wrapped this data key is not available (unknown or retired).");
            }

            var dataKey = new byte[ExperienceDataKey.SizeInBytes];
            using var aes = new AesGcm(kek, TagSize);
            try
            {
                aes.Decrypt(
                    wrapped[..NonceSize],
                    wrapped.Slice(NonceSize, ExperienceDataKey.SizeInBytes),
                    wrapped.Slice(NonceSize + ExperienceDataKey.SizeInBytes, TagSize),
                    dataKey,
                    KeyReferenceEncoding.Encode(reference, wrappedKey.KeyEncryptionKeyId));
            }
            catch
            {
                CryptographicOperations.ZeroMemory(dataKey);
                throw;
            }

            return ValueTask.FromResult(dataKey);
        }
    }

    private static void ValidateKey(string keyId, ReadOnlySpan<byte> key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"A key-encryption key is exactly {KeySize} bytes.", nameof(key));
        }
    }
}
