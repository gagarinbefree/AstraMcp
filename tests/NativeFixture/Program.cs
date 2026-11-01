// A purpose-built child process used only for tests; it never invokes the installed Astra.
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
if (args.Length != 4 || !args.Skip(1).SequenceEqual(new[] { "-i", "-n", "-q" })) return 21;
Console.WriteLine("fixture stdout: " + args[0]);
Console.Error.WriteLine("fixture stderr");
var mode = File.ReadAllText(args[0]).Trim();
if (mode is "desktop" or "desktop-hang") DesktopProbe.Report();
var replayConfiguration = Path.Combine(AppContext.BaseDirectory, "replay.json");
if (File.Exists(replayConfiguration)) return await ReplayFixture.RunAsync(args[0], replayConfiguration);
if (mode is "hang" or "desktop-hang") await Task.Delay(Timeout.Infinite);
return 0;
