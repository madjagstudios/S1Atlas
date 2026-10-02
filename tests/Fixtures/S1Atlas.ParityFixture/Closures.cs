namespace S1Atlas.ParityFixture;

public static class ClosureCases
{
    public static int NonCapturing()
    {
        Func<int, int> twice = static value => value * 2 + HelperTarget(0);
        return twice(21);
    }

    public static int Capturing(int seed)
    {
        var offset = seed + 1;
        Func<int, int> add = value => value + offset + HelperTarget(0);
        return add(1);
    }

    public static IEnumerable<int> Iterator(int count)
    {
        for (var index = 0; index < count; index++)
        {
            yield return HelperTarget(index);
        }
    }

    public static async Task<int> AsyncWork(int value)
    {
        await Task.Delay(1);
        return HelperTarget(value);
    }

    public static int StaticLocal(int value)
    {
        static int Triple(int inner) => inner * 3 + HelperTarget(0);
        return Triple(value);
    }

    public static int CapturingLocal(int value)
    {
        var factor = value;
        int Scale(int inner) => inner * factor + HelperTarget(0);
        return Scale(2);
    }

    public static int HelperTarget(int value) => value + 100;
}
