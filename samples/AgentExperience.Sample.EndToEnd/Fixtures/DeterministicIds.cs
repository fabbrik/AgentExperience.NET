using System.Globalization;

namespace AgentExperience.Sample.EndToEnd.Fixtures;

/// <summary>
/// A demonstration fixture, not an identifier source for real use: it hands out GUIDs from a
/// counter so that two consecutive runs of this sample produce a byte-identical transcript.
/// </summary>
/// <remarks>
/// A real host leaves <see cref="AgentExperience.MicrosoftAgentFramework.ExperienceCaptureOptions.NewId"/>
/// at its default, <see cref="Guid.NewGuid"/>. Sequential identifiers are safe here only because
/// nothing outside this process ever sees them.
/// </remarks>
internal sealed class DeterministicIds
{
    private int _issued;

    /// <summary>How many identifiers have been handed out so far.</summary>
    public int Issued => Volatile.Read(ref _issued);

    /// <summary>Hands out the next identifier in the sequence. Thread-safe, as capture requires.</summary>
    public Guid Next()
    {
        var n = Interlocked.Increment(ref _issued);
        return Guid.Parse(string.Format(CultureInfo.InvariantCulture, "ae000000-0000-4000-8000-{0:D12}", n));
    }
}
