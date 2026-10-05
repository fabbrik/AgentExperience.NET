using System.Security.Cryptography;
using AgentExperience.Abstractions;

namespace AgentExperience.Storage.Postgres.Vectors;

/// <summary>
/// How an encrypted deployment stores an embedding's content hash: never the plain
/// <see cref="ExperienceEmbeddingDescriptor.ComputeContentHash"/> value, which anyone holding a copy of the row could
/// confirm a guessed summary against, but an HMAC of it under a subkey of the record's own data key. Destroying that
/// key on erasure makes the stored value impossible to recompute, in every backup, replica and WAL segment alike, so it
/// no longer confirms a guessed summary. It is deterministic per record, so two copies still show whether a record's
/// summary changed between them.
/// </summary>
/// <remarks>
/// The stored form is <c>keyed:</c> followed by base64 of HMAC-SHA256(subkey, UTF-8(plain hash)), where the subkey is
/// HKDF-SHA256 of the record's data key with the info label <see cref="Purpose"/> and no salt. Core never sees it: the
/// re-index scan recomputes the plain hash from the opened summary, keys it, and reports the plain hash when the two
/// agree, so Core's "same model, same text" comparison is unchanged. Plaintext mode stores and reports the plain hash
/// exactly as before.
/// </remarks>
internal static class KeyedContentHash
{
    /// <summary>What every keyed content hash starts with. A plain hash is 64 lowercase hexadecimal characters.</summary>
    internal const string Prefix = "keyed:";

    /// <summary>The HKDF info label the content-hash subkey is derived with. Fixed: changing it re-embeds every record.</summary>
    internal const string Purpose = "aexp:embedding-content-hash:v1";

    /// <summary>
    /// What the scan reports for a stored hash it could not confirm (a different text or model, a plain hash on a record
    /// that has a key, a keyed hash on a record whose key the store never held). It contains a colon, so it can never equal a computed hash, and Core
    /// re-embeds the record.
    /// </summary>
    internal const string Unconfirmed = Prefix + "unconfirmed";

    internal static bool IsKeyed(string stored) => stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The stored form of <paramref name="plainHash"/> under <paramref name="key"/>.</summary>
    internal static string Key(RecordKey key, string plainHash) =>
        Prefix + Convert.ToBase64String(key.KeyedDigest(Purpose, plainHash));

    /// <summary>Whether <paramref name="stored"/> is <paramref name="plainHash"/> keyed under <paramref name="key"/>, compared in constant time.</summary>
    internal static bool Matches(RecordKey key, string plainHash, string stored)
    {
        if (!IsKeyed(stored))
        {
            return false;
        }

        byte[] storedDigest;
        try
        {
            storedDigest = Convert.FromBase64String(stored[Prefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(key.KeyedDigest(Purpose, plainHash), storedDigest);
    }
}
