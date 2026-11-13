using AgentExperience.Sample.Reflective;

// Runs the four arms on the same deterministic tasks and prints a Markdown table. Pass a path to also write it there.
var writer = new StringWriter();
var arms = await ReflectiveDemo.CompareArmsAsync(TextWriter.Null);

writer.WriteLine("| Arm | Verified success | Attempts | Tool calls | Model calls | Elapsed |");
writer.WriteLine("| --- | --- | --- | --- | --- | --- |");
foreach (var arm in arms)
{
    writer.WriteLine($"| {arm.Arm} | {arm.Verified}/{arm.Tasks} | {arm.Attempts} | {arm.ToolCalls} | {arm.ModelCalls} | {arm.Elapsed.TotalMilliseconds:F0} ms |");
}

writer.WriteLine();
writer.WriteLine("Scripted model: it applies what it is handed. This shows the mechanics (a retry needs reflection, a stored lesson removes the need for one). It says nothing about whether reflection or memory helps a real model, and it reports no tokens or cost.");

Console.Write(writer);
if (args.Length > 0)
{
    await File.WriteAllTextAsync(args[0], writer.ToString());
}
