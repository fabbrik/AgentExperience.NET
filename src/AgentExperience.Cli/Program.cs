using AgentExperience.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

return await AexpCli.RunAsync(
    args,
    new AexpConsole(Console.Out, Console.Error, Environment.CurrentDirectory, Environment.GetEnvironmentVariable, TimeProvider.System),
    cancellation.Token);
