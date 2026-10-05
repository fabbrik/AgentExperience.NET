using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentExperience.Abstractions;

namespace AgentExperience.Tests.Shared;

/// <summary>
/// Signs records as finalization would, from the documented canonical encodings (claims version 1, and version 2 of
/// story 17.2, whose content digest covers everything the writer renders), written independently of Core's internal signer so these tests can stand in for any release.
/// </summary>
internal static class SignedRecords
{
    /// <summary>The record with a claims version 2 signature over it as it stands.</summary>
    public static ExperienceRecord SignV2(ExperienceRecord record, string keyId, byte[] key) => record with
    {
        ProvenanceSignature = new ExperienceProvenanceSignature(
            keyId,
            ExperienceProvenanceSignature.HmacSha256ClaimsV2,
            HMACSHA256.HashData(key, Concat(Claims(record, "aexp-prov:v2"), SHA256.HashData(Content(record))))),
    };

    /// <summary>The record with a claims version 1 signature, as a release before story 17.2 signed it.</summary>
    public static ExperienceRecord SignV1(ExperienceRecord record, string keyId, byte[] key) => record with
    {
        ProvenanceSignature = new ExperienceProvenanceSignature(
            keyId, ExperienceProvenanceSignature.HmacSha256, HMACSHA256.HashData(key, Claims(record, "aexp-prov:v1"))),
    };

    /// <summary>How the PostgreSQL store serializes a payload: camel-case names, enums by name.</summary>
    private static readonly JsonSerializerOptions StoreJson = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(allowIntegerValues: false) },
    };

    /// <summary>
    /// A JSON number's exact decimal value as the documented canonical text: sign, significant digits, <c>E</c>,
    /// exponent; <c>0E0</c> for zero. Written from the number's text with a regular expression and big integers.
    /// </summary>
    public static string ExactDecimal(string number)
    {
        var match = System.Text.RegularExpressions.Regex.Match(number, @"^(-?)(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$");
        if (!match.Success)
        {
            throw new FormatException(number);
        }

        var digits = (match.Groups[2].Value + match.Groups[3].Value).TrimStart('0');
        var exponent = (match.Groups[4].Success ? System.Numerics.BigInteger.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : 0)
            - match.Groups[3].Value.Length;
        if (digits.Length == 0)
        {
            return "0E0";
        }

        var significant = digits.TrimEnd('0');
        exponent += digits.Length - significant.Length;
        return match.Groups[1].Value + significant + "E" + exponent.ToString(CultureInfo.InvariantCulture);
    }

    private static byte[] Concat(byte[] first, byte[] second) => [.. first, .. second];

    private static byte[] Claims(ExperienceRecord record, string tag)
    {
        var bytes = new Writer();
        bytes.Str(tag);
        bytes.Id(record.ExperienceId);
        bytes.Str(record.Scope.TenantId);
        bytes.Str(record.Scope.ApplicationId);
        bytes.Str(record.Scope.ProjectId);
        bytes.Str(record.Scope.TeamId);
        bytes.Str(record.Scope.AgentId);
        bytes.Str(record.Scope.UserId);
        bytes.Id(record.SourceRunId);
        if (record.ClosedRoundId is { } round)
        {
            bytes.Byte(1);
            bytes.Id(round);
        }
        else
        {
            bytes.Byte(0);
        }

        bytes.Int32((int)record.Origin);
        var exposures = record.Provenance.ExposedTo
            .Select(exposure => (Id: exposure.ExperienceId.ToByteArray(bigEndian: true), exposure.Revision))
            .OrderBy(exposure => Convert.ToHexString(exposure.Id), StringComparer.Ordinal)
            .ThenBy(exposure => exposure.Revision)
            .ToList();
        bytes.Int32(exposures.Count);
        foreach (var (id, revision) in exposures)
        {
            bytes.Raw(id);
            var buffer = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(buffer, revision);
            bytes.Raw(buffer);
        }

        return bytes.ToArray();
    }

    /// <summary>The documented content encoding (story 17.2), in its pinned field order.</summary>
    public static byte[] Content(ExperienceRecord record)
    {
        var bytes = new Writer();
        bytes.Str(record.TaskId);
        bytes.Str(record.TaskSummary);

        bytes.Byte(1);
        bytes.Int32((int)record.Outcome.Status);
        bytes.Int32(record.Outcome.Evidence.Count);

        bytes.Byte(1);
        bytes.Str(record.Environment.HostName);
        bytes.Str(record.Environment.RuntimeVersion);
        bytes.Str(record.Environment.OperatingSystem);
        bytes.Str(record.Environment.ApplicationVersion);
        bytes.Byte(1);
        bytes.Int32(record.Environment.Metadata.Count);
        foreach (var (key, value) in record.Environment.Metadata.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            bytes.Str(key);
            bytes.Str(value);
        }

        bytes.Byte(1);
        bytes.Int32(record.Attempts.Count);
        foreach (var attempt in record.Attempts)
        {
            bytes.Byte(1);
            bytes.Int32(attempt.SequenceNumber);
            bytes.Byte(attempt.Error is null ? (byte)0 : (byte)1);
            bytes.Byte(1);
            bytes.Int32(attempt.ToolCalls.Count);
            foreach (var call in attempt.ToolCalls)
            {
                bytes.Byte(1);
                bytes.Int32(call.SequenceNumber);
                bytes.Str(call.ToolName);
                bytes.Byte(1);
                bytes.Int32(call.Arguments.Count);
                foreach (var (key, value) in call.Arguments.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    bytes.Str(key);
                    bytes.Json(value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object), StoreJson));
                }
            }
        }

        if (record.Reflection is not { } reflection)
        {
            bytes.Byte(0);
            return bytes.ToArray();
        }

        bytes.Byte(1);
        bytes.Str(reflection.Lesson);
        bytes.List(reflection.SuccessfulApproaches);
        bytes.List(reflection.FailedApproaches);
        bytes.Str(reflection.ReuseGuidance);
        bytes.List(reflection.Preconditions);
        bytes.List(reflection.Warnings);
        bytes.Int32(reflection.EvidenceIds.Count);
        bytes.Int32((int)reflection.Authorship);
        bytes.Str(reflection.Producer);
        return bytes.ToArray();
    }

    private sealed class Writer
    {
        private readonly List<byte> _bytes = [];

        public void Byte(byte value) => _bytes.Add(value);

        public void Raw(byte[] value) => _bytes.AddRange(value);

        public void Id(Guid value) => _bytes.AddRange(value.ToByteArray(bigEndian: true));

        public void Int32(int value)
        {
            var buffer = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buffer, value);
            _bytes.AddRange(buffer);
        }

        public void Str(string? value)
        {
            if (value is null)
            {
                _bytes.Add(0);
                return;
            }

            _bytes.Add(1);
            var utf8 = Encoding.UTF8.GetBytes(value);
            Int32(utf8.Length);
            _bytes.AddRange(utf8);
        }

        public void List(IReadOnlyList<string>? values)
        {
            if (values is null)
            {
                _bytes.Add(0);
                return;
            }

            _bytes.Add(1);
            Int32(values.Count);
            foreach (var value in values)
            {
                Str(value);
            }
        }

        public void Json(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Null:
                    Byte(0);
                    break;
                case JsonValueKind.String:
                    Byte(1);
                    Str(value.GetString());
                    break;
                case JsonValueKind.True:
                    Byte(2);
                    break;
                case JsonValueKind.False:
                    Byte(3);
                    break;
                case JsonValueKind.Number:
                    Byte(4);
                    Str(ExactDecimal(value.GetRawText()));
                    break;
                case JsonValueKind.Array:
                    Byte(5);
                    Int32(value.GetArrayLength());
                    foreach (var item in value.EnumerateArray())
                    {
                        Json(item);
                    }

                    break;
                default:
                    Byte(6);
                    var members = value.EnumerateObject().GroupBy(member => member.Name, StringComparer.Ordinal)
                        .Select(group => group.Last())
                        .OrderBy(member => member.Name, StringComparer.Ordinal)
                        .ToList();
                    Int32(members.Count);
                    foreach (var member in members)
                    {
                        Str(member.Name);
                        Json(member.Value);
                    }

                    break;
            }
        }

        public byte[] ToArray() => [.. _bytes];
    }
}
