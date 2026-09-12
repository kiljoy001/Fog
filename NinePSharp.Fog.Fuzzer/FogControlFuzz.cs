using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NinePSharp.Fog;

namespace NinePSharp.Fuzzer;

/// <summary>Bounded inputs with a separate byte-list/replay model; invariant failures are never swallowed.</summary>
public static class FogControlFuzz
{
    private static readonly FogRecordSchema Schema = new("fixture-v1", ["id", "value"], ["id"], ["id"]);

    public static void Run(Stream input)
    {
        byte[] buffer = new byte[16385];
        int length = input.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        Run(buffer.AsSpan(0, length));
    }

    public static void Run(ReadOnlySpan<byte> bytes)
    {
        IReadOnlyList<IReadOnlyDictionary<string, string?>>? rows = null;
        try
        {
            rows = Schema.Parse(bytes, 16384, 16);
        }
        catch (FogException)
        {
            // Only declared protocol rejection is an acceptable parse outcome.
        }

        if (rows is not null)
        {
            Check(bytes.SequenceEqual(Schema.Serialize(rows, 16384, 16)), "record canonical round-trip");
        }

        var limits = new FogTransactionLimits(1, 1, 64, 64, 128, 1, TimeSpan.FromDays(1), TimeSpan.FromDays(1));
        var store = new FogTransactionStore(limits, _ => true);
        string id = store.Clone("owner");
        FogUpload? upload = null;
        bool writing = false;
        bool done = false;
        byte[]? sealedBytes = null;
        var pending = new List<byte>();
        int effects = 0;
        int expectedEffects = 0;

        foreach (byte instruction in bytes[..Math.Min(bytes.Length, 256)])
        {
            switch (instruction % 8)
            {
                case 0:
                    if (done || writing)
                    {
                        Reject("busy", () => store.OpenInput("owner", id, "session", "request"));
                    }
                    else
                    {
                        upload = store.OpenInput("owner", id, "session", "request");
                        writing = true;
                        pending.Clear();
                    }

                    break;
                case 1:
                    if (upload is null)
                    {
                        break;
                    }

                    if (!writing)
                    {
                        Reject(done ? "busy" : "upload-open", () => upload.Write(0, [instruction]));
                    }
                    else if (pending.Count + (sealedBytes?.Length ?? 0) == 64)
                    {
                        Reject("limit", () => upload.Write((ulong)pending.Count, [instruction]));
                    }
                    else
                    {
                        upload.Write((ulong)pending.Count, [instruction]);
                        pending.Add(instruction);
                    }

                    break;
                case 2:
                    if (upload is null)
                    {
                        break;
                    }

                    if (!writing)
                    {
                        Reject(done ? "busy" : "upload-open", upload.Seal);
                    }
                    else
                    {
                        upload.Seal();
                        sealedBytes = pending.ToArray();
                        writing = false;
                    }

                    break;
                case 3:
                    if (!done && (writing || sealedBytes is null))
                    {
                        Reject("upload-open", () => store.CommitAsync("owner", id, _ => throw new InvalidOperationException("preparation before sealing")));
                    }
                    else
                    {
                        var control = new FogControlFile(store, inputs =>
                        {
                            Check(!done, "completed transaction prepared twice");
                            Check(inputs["request"].Span.SequenceEqual(sealedBytes), "sealed input differs from model");
                            return new FogCommitPlan(new Dictionary<string, byte[]> { ["reply"] = inputs["request"].ToArray() },
                                () => { effects++; return System.Threading.Tasks.Task.CompletedTask; });
                        });
                        Check(control.WriteAsync("owner", id, "commit\n"u8.ToArray()).GetAwaiter().GetResult() == 7, "commit byte count");
                        if (!done)
                        {
                            expectedEffects++;
                        }

                        done = true;
                    }

                    break;
                case 4:
                    upload?.Dispose();
                    writing = false;
                    break;
                case 5:
                    store.CloseSession("session");
                    writing = false;
                    break;
                case 6:
                    var release = new FogControlFile(store, _ => throw new InvalidOperationException("release prepared an effect"));
                    Check(release.WriteAsync("owner", id, "release\n"u8.ToArray()).GetAwaiter().GetResult() == 8, "release byte count");
                    Reject("tx-expired", () => store.Status("owner", id));
                    upload?.Dispose();
                    upload = null;
                    string next = store.Clone("owner");
                    Check(id != next, "released identity reused");
                    id = next;
                    done = writing = false;
                    sealedBytes = null;
                    pending.Clear();
                    break;
                case 7:
                    var invalid = new FogControlFile(store, _ => throw new InvalidOperationException("invalid command prepared an effect"));
                    Reject("invalid-request", () => invalid.WriteAsync("owner", id, new byte[] { instruction }).GetAwaiter().GetResult());
                    if (writing)
                    {
                        Reject("invalid-request", () => upload!.Write(ulong.MaxValue, [instruction]));
                    }

                    if (done)
                    {
                        ulong offset = (ulong)(instruction >> 3);
                        byte[] expected = sealedBytes!.Skip((int)offset).Take(3).ToArray();
                        Check(expected.SequenceEqual(store.ReadOutput("owner", id, "reply", offset, 3)), "snapshot slice differs from model");
                    }
                    else
                    {
                        Reject("not-ready", () => store.ReadOutput("owner", id, "reply", 0, 1));
                    }

                    break;
            }

            Check(effects == expectedEffects, "effect without exactly one accepted commit");
            Check(store.Status("owner", id).State == (done ? "done" : "staging"), "state differs from model");
        }

        upload?.Dispose();
        store.Release("owner", id);
    }

    private static void Reject(string code, Action operation)
    {
        try
        {
            operation();
        }
        catch (FogException exception)
        {
            Check(exception.Code == code, "unexpected rejection");
            return;
        }

        throw new InvalidOperationException("invalid operation accepted: " + code);
    }

    private static void Check(bool condition, string invariant)
    {
        if (!condition)
        {
            throw new InvalidOperationException(invariant);
        }
    }
}
