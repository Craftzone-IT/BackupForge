using System.Text;
using BackupForge.Cli;

// CR-002 A7.: redirected output (SSH, | Out-File, scheduled task) is UTF-8 without BOM instead of the console code page.
// The interactive console is left alone (Console.OutputEncoding would also change the code page of the parent console).
var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
if (Console.IsOutputRedirected) Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
if (Console.IsErrorRedirected) Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });

return await BfCli.RunAsync(args, Console.Out, Console.Error, Console.In).ConfigureAwait(false);
