using System.Threading;

namespace S1Atlas.ParityFixture;

public struct MutablePoint
{
    public int X;
    public int Y;
}

public sealed class RefFieldCases
{
    public int Counter;
    public static int Shared;
    public MutablePoint Point;

    public int ReadCounter() => Counter;

    public void WriteCounter(int value) => Counter = value;

    public void BumpViaRef() => Bump(ref Counter);

    public void BumpSharedViaInterlocked() => Interlocked.Increment(ref Shared);

    public int BumpSharedViaCompareExchange() => Interlocked.CompareExchange(ref Shared, 1, 0);

    public void MovePoint(int dx) => Point.X += dx;

    private static void Bump(ref int slot) => slot++;
}
