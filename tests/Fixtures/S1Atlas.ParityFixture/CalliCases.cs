namespace S1Atlas.ParityFixture;

public static unsafe class CalliCases
{
    public static int StaticTarget(int value) => value * 2;

    public static int InvokeViaPointer(delegate*<int, int> pointer, int value) => pointer(value);

    public static delegate*<int, int> PointerToStatic() => &StaticTarget;
}
