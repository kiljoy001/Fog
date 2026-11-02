using NinePSharp.Fog.Client;

// fog on the console it is run from: System.Console and the machine as ConsoleLocal takes them.
return await Rcpu.RunAsync(args, ConsoleLocal.Create(new SystemConsole()), CancellationToken.None);
