using System.Runtime.CompilerServices;
using System.Threading;

// Authored compiler-diagnostic fixture: no WebRTC/package/provider dependency.
return Nested(1) == 2 && Nested(0) == 7 && Volatile.Read(ref State.Finalized) == 2 ? 0 : 1;

[MethodImpl(MethodImplOptions.NoInlining)]
static int Nested(int value)
{
    try
    {
        try
        {
            if (value == 0) throw new InvalidOperationException("Synthetic exception");
            return value + 1;
        }
        catch (InvalidOperationException) { return 7; }
    }
    finally { Interlocked.Increment(ref State.Finalized); }
}

internal static class State { internal static int Finalized; }
