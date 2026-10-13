using NinePSharp.Fog.Wasm;

using Stream stdin = Console.OpenStandardInput();
using Stream stdout = Console.OpenStandardOutput();
using Stream stderr = Console.OpenStandardError();
return await WasiRunner.RunAsync(args, stdin, stdout, stderr);
