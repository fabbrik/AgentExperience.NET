using AgentExperience.Sample.Reflective;

// The model: a scripted stand-in, so this needs no key and no network. Replace it with any IChatClient (for example
// new OpenAIClient(key).GetChatClient("gpt-4.1-mini").AsIChatClient()) to try a real model; the reflection loop, the
// host's own build check and the stored lessons work the same way.
using var model = new ReflectiveDemo.ScriptedModel();
await ReflectiveDemo.RunAsync(model, Console.Out);

if (args.Contains("--compare", StringComparer.Ordinal))
{
    // Mechanics, not evidence: the scripted model obeys whatever it is handed. See README.md.
    Console.WriteLine();
    await ReflectiveDemo.CompareArmsAsync(Console.Out);
}
