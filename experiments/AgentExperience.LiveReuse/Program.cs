using AgentExperience.LiveReuse;

// The explicit command, and the only way this experiment ever calls a model:
//
//   dotnet run --project experiments/AgentExperience.LiveReuse -c Release
//
// With no provider variable set it prints how to configure one and exits 0, having spent nothing. `--scripted` runs
// the whole harness offline against the scripted stand-in and prints the report without writing a file. CI never runs
// this project: `dotnet test` does not run console apps, and no workflow invokes it.
return await LiveReuseHost.RunAsync(args, Environment.GetEnvironmentVariable, Console.Out, Console.Error, TimeProvider.System);
