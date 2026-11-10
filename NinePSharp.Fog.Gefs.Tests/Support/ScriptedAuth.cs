using System.Text;

namespace NinePSharp.Fog.Gefs.Tests.Support;

// factotum's p9any as a server, scripted: it asks "proof?" until the client writes a name, and is
// then done, the client being the user named. A name of "-" leaves the authinfo without a user,
// "garbage" is refused, and "confused" puts it in a state of error; once done, a read is out of
// phase.
internal sealed class ScriptedAuth : IGefsAuth
{
    private string? written;
    private bool done;

    public int Disposed { get; private set; }

    public Task<(GefsAuthResult Result, byte[] Data)> ReadAsync()
    {
        if (done)
        {
            return Task.FromResult((GefsAuthResult.Phase, Array.Empty<byte>()));
        }

        if (written is null)
        {
            return Task.FromResult((GefsAuthResult.Ok, "proof?"u8.ToArray()));
        }

        if (written == "confused")
        {
            return Task.FromResult((GefsAuthResult.Other, Array.Empty<byte>()));
        }

        done = true;
        return Task.FromResult((GefsAuthResult.Done, Array.Empty<byte>()));
    }

    public Task<bool> WriteAsync(byte[] data)
    {
        string text = Encoding.UTF8.GetString(data);
        written = text == "garbage" ? written : text;
        return Task.FromResult(text != "garbage");
    }

    public Task<string?> CuidAsync() => Task.FromResult(written == "-" ? null : written);

    public ValueTask DisposeAsync()
    {
        Disposed++;
        return ValueTask.CompletedTask;
    }
}
