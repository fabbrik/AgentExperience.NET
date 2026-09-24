using System.Runtime.CompilerServices;
using DiffEngine;

namespace AgentExperience.Release.Tests;

/// <summary>
/// Process-wide Verify configuration for the public API baseline. Nothing here ever accepts a change on
/// its own: accepting is opt-in, per run, through <see cref="AcceptVariable"/>.
/// </summary>
internal static class VerifySettings
{
    /// <summary>Set to <c>true</c> to accept every received public API as the new baseline for one run.</summary>
    internal const string AcceptVariable = "AGENTEXPERIENCE_ACCEPT_API_CHANGES";

    [ModuleInitializer]
    internal static void Initialize()
    {
        // Never launch a diff tool: the diff is read in git, not in a pop-up on whoever ran the tests.
        DiffRunner.Disabled = true;

        if (string.Equals(Environment.GetEnvironmentVariable(AcceptVariable), "true", StringComparison.OrdinalIgnoreCase))
        {
            VerifyTests.VerifierSettings.AutoVerify();
        }
    }
}
