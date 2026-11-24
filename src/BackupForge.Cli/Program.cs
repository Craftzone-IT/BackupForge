using BackupForge.Cli;

return await BfCli.RunAsync(args, Console.Out, Console.Error, Console.In).ConfigureAwait(false);
