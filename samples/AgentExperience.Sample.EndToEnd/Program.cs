using AgentExperience.Sample.EndToEnd;

// The whole entry point. `dotnet run --project samples/AgentExperience.Sample.EndToEnd` needs the
// .NET 10 SDK and nothing else: no Docker, no PostgreSQL, no model credentials, no network. Set
// AGENTEXPERIENCE_SAMPLE_POSTGRES to a connection string to run the same seven stages against the
// real PostgreSQL adapters instead.
return await SampleHost.RunAsync(
    Console.Out,
    Environment.GetEnvironmentVariable(SampleHost.PostgresEnvironmentVariable));
