using System.IO.Pipelines;
using AgentExperience.Cli.Configuration;
using AgentExperience.Cli.Engine;
using AgentExperience.Cli.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentExperience.Cli.Tests;

/// <summary>The server on one end of two pipes, the SDK's client on the other.</summary>
public sealed class McpTestSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Pipe _toServer = new();
    private readonly Pipe _toClient = new();
    private System.Threading.Tasks.Task _server = System.Threading.Tasks.Task.CompletedTask;

    public McpClient Client { get; private set; } = null!;

    /// <summary>What the server wrote for its operator.</summary>
    public StringWriter Log { get; } = new();

    public static async Task<McpTestSession> StartAsync(TempProject project)
    {
        var session = new McpTestSession();
        var settings = AexpConfigLoader.Load(null, project.Root, project.Environment);
        var engine = new AexpEngine(settings, TimeProvider.System, project.Environment);
        session._server = AexpMcpServer.RunAsync(engine, session._toServer.Reader.AsStream(), session._toClient.Writer.AsStream(), session.Log, session._stop.Token);
        session.Client = await McpClient.CreateAsync(
            new StreamClientTransport(session._toServer.Writer.AsStream(), session._toClient.Reader.AsStream()));
        return session;
    }

    public static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    public async Task<string> CallAsync(string tool, Dictionary<string, object?> arguments)
    {
        var result = await Client.CallToolAsync(tool, arguments);
        Assert.False(result.IsError == true, $"{tool} failed: {Text(result)}");
        return Text(result);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _stop.CancelAsync();
        await _toServer.Writer.CompleteAsync();
        try
        {
            await _server;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }
}
