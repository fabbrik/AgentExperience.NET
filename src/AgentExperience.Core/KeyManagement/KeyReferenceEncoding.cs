using System.Buffers.Binary;
using System.Text;
using AgentExperience.Abstractions;

namespace AgentExperience.Core.KeyManagement;

/// <summary>
/// The canonical byte encoding of an <see cref="ExperienceKeyReference"/>, used as AEAD associated data so a
/// wrapped key is bound to exactly one record. Every component is length-prefixed (a null field is length
/// <c>-1</c>, distinct from the empty string), so no two different references encode to the same bytes.
/// </summary>
internal static class KeyReferenceEncoding
{
    private const string Label = "AgentExperience.NET/wrapped-data-key/v1";

    internal static byte[] Encode(ExperienceKeyReference reference, string keyEncryptionKeyId)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(reference.Scope);

        using var stream = new MemoryStream();
        Write(stream, Label);
        Write(stream, keyEncryptionKeyId);
        Write(stream, reference.ExperienceId.ToString("D"));
        Write(stream, reference.Scope.TenantId);
        Write(stream, reference.Scope.ApplicationId);
        Write(stream, reference.Scope.ProjectId);
        Write(stream, reference.Scope.TeamId);
        Write(stream, reference.Scope.AgentId);
        Write(stream, reference.Scope.UserId);
        return stream.ToArray();
    }

    internal static void Validate(ExperienceKeyReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(reference.Scope);
        if (reference.ExperienceId == Guid.Empty)
        {
            throw new ArgumentException("A key reference names a record; its ExperienceId must not be empty.", nameof(reference));
        }
    }

    private static void Write(Stream stream, string? value)
    {
        Span<byte> length = stackalloc byte[4];
        if (value is null)
        {
            BinaryPrimitives.WriteInt32BigEndian(length, -1);
            stream.Write(length);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }
}
