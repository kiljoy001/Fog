using FsCheck.Xunit;
using NinePSharp.Fuzzer;
using Xunit;

namespace NinePSharp.Fog.Tests;

public sealed class FogModelTests
{
    [Property(MaxTest = 300)]
    public void TransactionCommandSequencesAgreeWithIndependentModel(byte[] commands) => FogControlFuzz.Run(commands);

    [Fact]
    public void FuzzerSeedsExerciseCompleteAndRejectedTransitions()
    {
        FogControlFuzz.Run(new byte[] { 3, 0, 0, 1, 7, 2, 2, 1, 3, 3, 1, 2, 7, 4, 5, 6, 0, 1, 4, 1, 2, 0, 1, 5, 1, 2 });
        FogControlFuzz.Run(new byte[] { 0 }.Concat(Enumerable.Repeat((byte)1, 66)).Concat(new byte[] { 2, 0, 1, 4, 3, 7 }).ToArray());
        using var stream = new MemoryStream("schema=fixture-v1\n\tcol=id\n\tcol=value\n\nid=one\n\n"u8.ToArray());
        FogControlFuzz.Run(stream);
        using var oversized = new MemoryStream(new byte[20000]);
        FogControlFuzz.Run(oversized);
    }
}
