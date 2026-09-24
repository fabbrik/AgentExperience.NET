using System.Collections;

namespace AgentExperience.Core.Tests;

/// <summary>
/// A hostile <see cref="IReadOnlyList{T}"/>: the first enumeration yields <c>first</c>, every later
/// read (enumeration, indexer or count) yields <c>later</c>. It models a caller or host list that
/// changes after it has been checked.
/// </summary>
internal sealed class FlippingList<T>(IReadOnlyList<T> first, IReadOnlyList<T> later) : IReadOnlyList<T>
{
    private bool _enumerated;

    public int Count => (_enumerated ? later : first).Count;

    public T this[int index] => (_enumerated ? later : first)[index];

    public IEnumerator<T> GetEnumerator()
    {
        var current = _enumerated ? later : first;
        _enumerated = true;
        return current.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
