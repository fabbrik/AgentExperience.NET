using System.Net.Sockets;
using AgentExperience.Abstractions;
using Npgsql;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// Decides which failures are infrastructure failures and translates them, for the record, grant and reuse-feedback
/// stores, the grant access log, the candidate source and the Vectors package's embedding index and its index maintenance.
/// </summary>
internal static class ExperienceStoreFailures
{
    /// <summary>
    /// Driver, socket, and timeout failures are translated. An <see cref="OperationCanceledException"/>
    /// caused by the caller's own token is not matched, so it propagates unwrapped with its stack.
    /// </summary>
    internal static bool IsInfrastructureFailure(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        NpgsqlException or SocketException or TimeoutException => true,
        _ => false,
    };

    internal static Exception Translate(Exception ex, string operation, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled while the driver reported a failure: surface cancellation, unwrapped.
            return new OperationCanceledException("The Experience Record store operation was cancelled.", ex, cancellationToken);
        }

        return new ExperienceStoreException($"Experience Record {operation} failed due to a storage infrastructure error.", ex);
    }
}
