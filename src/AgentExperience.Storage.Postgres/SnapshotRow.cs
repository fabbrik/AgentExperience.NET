using System.Collections;
using System.Data.Common;
using System.Globalization;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// One row copied out of a data reader, readable through the same <see cref="DbDataReader"/> surface -- by
/// ordinal and by column name -- so the row decoders read it exactly as they read the live reader. The
/// retrieval reads (text search, vector search, <c>GetManyAsync</c>) snapshot their rows, close the reader,
/// commit their read-only transaction and release the connection to the pool, fetch every sealed row's key
/// in one batch, and only then decode: no key-store call is made while a reader, a transaction or a pooled
/// connection is held. The snapshot is bounded by the statement's own <c>LIMIT</c> (the candidate limit) or,
/// for <c>GetManyAsync</c>, by the ID list it was given.
/// </summary>
/// <remarks>
/// It is always positioned on its one row. Values are what <see cref="DbDataReader.GetValues"/> returned, and
/// each column's <see cref="GetFieldType"/> and <see cref="GetDataTypeName"/> are the live reader's, null or
/// not. Reads convert as the live reader does for the columns these decoders read: a UTC
/// <see cref="DateTime"/> (<c>timestamptz</c>) reads as the UTC <see cref="DateTimeOffset"/> (any other kind
/// throws), and a number widens to a larger numeric type, for nullable targets too. Anything else that does
/// not match the requested type throws <see cref="InvalidCastException"/>, as schema drift does on the live
/// reader.
/// </remarks>
internal sealed class SnapshotRow : DbDataReader
{
    private readonly Columns _columns;
    private readonly object[] _values;

    private SnapshotRow(Columns columns, object[] values)
    {
        _columns = columns;
        _values = values;
    }

    /// <summary>Reads every remaining row of <paramref name="reader"/> into memory. The caller then disposes the reader.</summary>
    internal static async Task<List<SnapshotRow>> ReadAllAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        var count = reader.FieldCount;
        var columns = new Columns(new string[count], new Type[count], new string[count]);
        for (var i = 0; i < count; i++)
        {
            columns.Names[i] = reader.GetName(i);
            columns.Types[i] = reader.GetFieldType(i);
            columns.TypeNames[i] = reader.GetDataTypeName(i);
        }

        var rows = new List<SnapshotRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new object[count];
            reader.GetValues(values);
            rows.Add(new SnapshotRow(columns, values));
        }

        return rows;
    }

    public override int FieldCount => _values.Length;

    public override int Depth => 0;

    public override bool HasRows => true;

    public override bool IsClosed => false;

    public override int RecordsAffected => -1;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override string GetName(int ordinal) => _columns.Names[ordinal];

    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < _columns.Names.Length; i++)
        {
            if (string.Equals(_columns.Names[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        for (var i = 0; i < _columns.Names.Length; i++)
        {
            if (string.Equals(_columns.Names[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        // What NpgsqlDataReader throws, and what the by-name readers treat as "not selected".
        throw new IndexOutOfRangeException($"Field not found in row: {name}");
    }

    public override object GetValue(int ordinal) => _values[ordinal];

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, _values.Length);
        Array.Copy(_values, values, count);
        return count;
    }

    public override bool IsDBNull(int ordinal) => _values[ordinal] is DBNull;

    public override T GetFieldValue<T>(int ordinal)
    {
        var value = _values[ordinal];
        var target = Nullable.GetUnderlyingType(typeof(T));
        if (value is DBNull)
        {
            // As the live reader: a nullable value type reads as null, object as DBNull, anything else throws.
            return target is not null
                ? default!
                : typeof(T) == typeof(object)
                    ? (T)value
                    : throw new InvalidCastException($"Column '{_columns.Names[ordinal]}' is null.");
        }

        if (value is T typed)
        {
            return typed;
        }

        target ??= typeof(T);
        if (target == typeof(DateTimeOffset) && value is DateTime timestamp)
        {
            // A timestamptz arrives as a UTC DateTime; any other kind is a column the live reader would refuse.
            return timestamp.Kind == DateTimeKind.Utc
                ? (T)(object)new DateTimeOffset(timestamp)
                : throw new InvalidCastException(
                    $"Column '{_columns.Names[ordinal]}' holds a {timestamp.Kind} DateTime, which does not read as a DateTimeOffset.");
        }

        if (Widens(value.GetType(), target))
        {
            return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }

        return (T)value;
    }

    public override bool GetBoolean(int ordinal) => GetFieldValue<bool>(ordinal);

    public override byte GetByte(int ordinal) => GetFieldValue<byte>(ordinal);

    public override char GetChar(int ordinal) => GetFieldValue<char>(ordinal);

    public override DateTime GetDateTime(int ordinal) => GetFieldValue<DateTime>(ordinal);

    public override decimal GetDecimal(int ordinal) => GetFieldValue<decimal>(ordinal);

    public override double GetDouble(int ordinal) => GetFieldValue<double>(ordinal);

    public override float GetFloat(int ordinal) => GetFieldValue<float>(ordinal);

    public override Guid GetGuid(int ordinal) => GetFieldValue<Guid>(ordinal);

    public override short GetInt16(int ordinal) => GetFieldValue<short>(ordinal);

    public override int GetInt32(int ordinal) => GetFieldValue<int>(ordinal);

    public override long GetInt64(int ordinal) => GetFieldValue<long>(ordinal);

    public override string GetString(int ordinal) => GetFieldValue<string>(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException("A snapshot row does not stream.");

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException("A snapshot row does not stream.");

    public override string GetDataTypeName(int ordinal) => _columns.TypeNames[ordinal];

    public override Type GetFieldType(int ordinal) => _columns.Types[ordinal];

    public override IEnumerator GetEnumerator() => throw new NotSupportedException("A snapshot row is one row.");

    public override bool NextResult() => false;

    public override bool Read() => false;

    /// <summary>Whether C# has an implicit numeric conversion from <paramref name="source"/> to <paramref name="target"/>.</summary>
    private static bool Widens(Type source, Type target) =>
        Type.GetTypeCode(source) switch
        {
            TypeCode.Byte => Type.GetTypeCode(target) is TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32
                or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
            TypeCode.Int16 => Type.GetTypeCode(target) is TypeCode.Int32 or TypeCode.Int64 or TypeCode.Single or TypeCode.Double
                or TypeCode.Decimal,
            TypeCode.Int32 => Type.GetTypeCode(target) is TypeCode.Int64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
            TypeCode.Int64 => Type.GetTypeCode(target) is TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
            TypeCode.Single => Type.GetTypeCode(target) is TypeCode.Double,
            _ => false,
        } && !target.IsEnum;

    /// <summary>The column metadata every row of one snapshot shares.</summary>
    private sealed record Columns(string[] Names, Type[] Types, string[] TypeNames);
}
