// Story 12.1: the crypto-shredding key store an encrypted seeder writes and the upgrade tests re-open. Compiled into
// every seeder (against that preview's key-management contracts) and linked into tests/AgentExperience.Upgrade.Tests
// (against today's). The contracts are identical across the two, which is what lets a key the published preview
// wrapped be unwrapped by today's code. Test fixture only: the key-encryption key is a constant, and the wrapped keys
// live in a plain JSON file. 0.1.0-preview.1 had no crypto-shredding, so its seeder compiles none of this.

#if !PREVIEW1
using System.Text.Json;
using AgentExperience.Abstractions;
using AgentExperience.Core.KeyManagement;

namespace AgentExperience.Upgrade.Seeders;

/// <summary>An <see cref="IExperienceWrappedKeyRepository"/> kept in one JSON file, so a second process can re-open it.</summary>
internal sealed class FileWrappedKeyRepository(string path) : IExperienceWrappedKeyRepository
{
    /// <summary>The key-encryption key's ID. Its material is <see cref="KeyEncryptionKeyMaterial"/>.</summary>
    public const string KeyEncryptionKeyId = "upgrade-test-kek";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();

    /// <summary>A fixed, test-only 32-byte key-encryption key: the seeder and the tests must wrap and unwrap alike.</summary>
    public static byte[] KeyEncryptionKeyMaterial() => Enumerable.Range(0, 32).Select(i => (byte)(0xA5 ^ (i * 7))).ToArray();

    /// <summary>The envelope key store over this file and the fixed key-encryption key.</summary>
    public static EnvelopeExperienceKeyStore OpenKeyStore(string path) =>
        new(new LocalExperienceKeyEncryptionKey(KeyEncryptionKeyId, KeyEncryptionKeyMaterial()), new FileWrappedKeyRepository(path));

    public ValueTask<ExperienceWrappedKeyEntry?> GetAsync(ExperienceKeyReference reference, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var entry = Load().FirstOrDefault(e => e.Matches(reference));
            return ValueTask.FromResult(entry?.ToEntry());
        }
    }

    public ValueTask<ExperienceWrappedKeyEntry> AddIfAbsentAsync(ExperienceKeyReference reference, ExperienceWrappedKey wrappedKey, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var entries = Load();
            var existing = entries.FirstOrDefault(e => e.Matches(reference));
            if (existing is null)
            {
                existing = StoredEntry.From(reference, wrappedKey);
                entries.Add(existing);
                Save(entries);
            }

            return ValueTask.FromResult(existing.ToEntry());
        }
    }

    public ValueTask<bool> ReplaceAsync(ExperienceKeyReference reference, ExperienceWrappedKey expected, ExperienceWrappedKey replacement, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var entries = Load();
            var index = entries.FindIndex(e => e.Matches(reference));
            if (index < 0 || entries[index].ToEntry().WrappedKey is not { } stored
                || stored.KeyEncryptionKeyId != expected.KeyEncryptionKeyId
                || !stored.Ciphertext.Span.SequenceEqual(expected.Ciphertext.Span))
            {
                return ValueTask.FromResult(false);
            }

            entries[index] = StoredEntry.From(reference, replacement);
            Save(entries);
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask DestroyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var entries = Load();
            entries.RemoveAll(e => e.Matches(reference));
            entries.Add(StoredEntry.From(reference, wrappedKey: null));
            Save(entries);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<ExperienceWrappedKeyEntry>> ListNotWrappedUnderAsync(string currentKeyEncryptionKeyId, int limit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<ExperienceWrappedKeyEntry> page = Load()
                .Where(e => e.KeyEncryptionKeyId is { } id && id != currentKeyEncryptionKeyId)
                .Take(limit)
                .Select(e => e.ToEntry())
                .ToArray();
            return ValueTask.FromResult(page);
        }
    }

    private List<StoredEntry> Load() =>
        File.Exists(path) ? JsonSerializer.Deserialize<List<StoredEntry>>(File.ReadAllText(path), Json) ?? [] : [];

    private void Save(List<StoredEntry> entries) => File.WriteAllText(path, JsonSerializer.Serialize(entries, Json));

    /// <summary>One reference's key: wrapped, or destroyed (no key material, never to be given one again).</summary>
    internal sealed record StoredEntry(Guid ExperienceId, Scope Scope, string? KeyEncryptionKeyId, string? Ciphertext)
    {
        public bool Matches(ExperienceKeyReference reference) => ExperienceId == reference.ExperienceId && Scope == reference.Scope;

        public ExperienceWrappedKeyEntry ToEntry() => new(
            new ExperienceKeyReference(ExperienceId, Scope),
            KeyEncryptionKeyId is null ? null : new ExperienceWrappedKey(KeyEncryptionKeyId, Convert.FromBase64String(Ciphertext!)));

        public static StoredEntry From(ExperienceKeyReference reference, ExperienceWrappedKey? wrappedKey) => new(
            reference.ExperienceId,
            reference.Scope,
            wrappedKey?.KeyEncryptionKeyId,
            wrappedKey is null ? null : Convert.ToBase64String(wrappedKey.Ciphertext.Span));
    }
}
#endif
