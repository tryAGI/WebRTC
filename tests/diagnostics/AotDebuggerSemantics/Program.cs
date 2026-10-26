using System.Runtime.CompilerServices;
using System.Threading;

return EhDebugProbe.Probe(1) == 111 && EhDebugProbe.Probe(0) == 120 && Volatile.Read(ref EhDebugProbe.Last) == 130 ? 0 : 1;

internal static class EhDebugProbe
{
    internal static int Last;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Probe(int input)
    {
        int outer = input + 100;
        try
        {
            int tryOnly = outer + 10;
            Checkpoint(1, tryOnly); // DEBUG_TRY
            Sink(outer);
            Sink(tryOnly);
            if (input == 0) throw new InvalidOperationException("Authored debugger fixture");
            return tryOnly;
        }
        catch (InvalidOperationException)
        {
            int catchOnly = outer + 20;
            Checkpoint(2, catchOnly); // DEBUG_CATCH
            Sink(outer);
            Sink(catchOnly);
            return catchOnly;
        }
        finally
        {
            int finallyOnly = outer + 30;
            Checkpoint(3, finallyOnly); // DEBUG_FINALLY
            Sink(outer);
            Sink(finallyOnly);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Checkpoint(int stage, int value)
    {
        Volatile.Write(ref Last, stage + value);
        HoldArguments(stage, value);
    }

    // Keep both checkpoint arguments live at its source/prologue breakpoint.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void HoldArguments(int stage, int value) => Volatile.Write(ref Last, stage ^ value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(int value) => Volatile.Write(ref Last, value);
}
