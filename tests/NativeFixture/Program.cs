// A purpose-built child process used only for tests; it never invokes the installed Astra.
if (args.Length != 4 || !args.Skip(1).SequenceEqual(new[] { "-i", "-n", "-q" })) return 21;
Console.WriteLine("fixture stdout: " + args[0]);
Console.Error.WriteLine("fixture stderr");
var replayConfiguration = Path.Combine(AppContext.BaseDirectory, "replay.json");
if (File.Exists(replayConfiguration)) return await ReplayFixture.RunAsync(args[0], replayConfiguration);
if (File.ReadAllText(args[0]).Trim() == "hang") await Task.Delay(Timeout.Infinite);
return 0;
