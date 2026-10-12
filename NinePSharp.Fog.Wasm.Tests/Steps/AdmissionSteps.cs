using FsCheck;
using FsCheck.Fluent;
using NinePSharp.Fog.Kernel;
using Reqnroll;

namespace NinePSharp.Fog.Wasm.Tests.Steps;

[Binding]
public sealed class AdmissionSteps
{
    private readonly List<byte[]> admitted = [];
    private Process? process;
    private Arbitrary<(int Image, int At, byte To)>? changes;

    [Given("the applications the other scenarios admit")]
    public async Task GivenAdmitted()
    {
        process = await FogKernel.InMemory(new Dictionary<string, ProgramMain>()).BootAsync();
        foreach (string fixture in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "*.wasm").Order())
        {
            byte[] image = File.ReadAllBytes(fixture);
            if (Loads(image))
            {
                admitted.Add(image);
            }
        }
    }

    [When("any one byte of one of them is changed")]
    public void WhenChanged()
        => changes = Gen.Choose(0, admitted.Count - 1)
            .SelectMany(image => Gen.Choose(0, admitted[image].Length - 1).Select(at => (image, at)))
            .SelectMany(change => Gen.Choose(0, 255).Select(to => (change.image, change.at, (byte)to)))
            .ToArbitrary();

    [Then(@"^loading it either gives a program or fails with ""exec header invalid""$")]
    public void ThenLoadsOrRefuses()
        => Prop.ForAll(changes!, change =>
            {
                byte[] image = [.. admitted[change.Image]];
                image[change.At] = change.To;
                Loads(image);
                return true;
            })
            .Check(Config.QuickThrowOnFailure.WithMaxTest(1000));

    // Whether the image loads; any failure but exec's refusal escapes.
    private bool Loads(byte[] image)
    {
        try
        {
            Preview1Module.Load(image, process!, TimeProvider.System);
            return true;
        }
        catch (SyscallException refused) when (refused.Message == "exec header invalid")
        {
            return false;
        }
    }
}
