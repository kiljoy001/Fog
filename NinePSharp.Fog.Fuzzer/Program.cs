namespace NinePSharp.Fuzzer;

// The Fog fuzz targets, named as scripts/fuzz.sh names them.
public static class Program
{
    public static void Main(string[] args)
    {
        Action<Stream> target = args.FirstOrDefault() switch
        {
            "fog" => FogControlFuzz.Run,
            "fog-files" => FogFileFuzz.Run,
            "fog-dispatcher" => FogDispatcherFuzz.Run,
            _ => throw new ArgumentException("usage: NinePSharp.Fog.Fuzzer fog|fog-files|fog-dispatcher"),
        };
        SharpFuzz.Fuzzer.OutOfProcess.Run(target);
    }
}
