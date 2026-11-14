using AgentExperience.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

return await AexpCli.RunAsync(
    args,
    new AexpConsole(
        Console.Out,
        Console.Error,
        Environment.CurrentDirectory,
        Environment.GetEnvironmentVariable,
        TimeProvider.System,
        // A question is asked only of a person at a terminal, never of a pipe.
        ReadLine: Console.IsInputRedirected ? null : Console.ReadLine),
    cancellation.Token);
