namespace S1Atlas.ParityFixture;

public static class GeneratedBodies
{
    public static async Task<int> NestedAsync(int value)
    {
        Func<int, int> twice = x => x * 2 + Leaf(x);
        await Task.Delay(1);
        return twice(value);
    }

    public static int LocalCapturingLambda(int value)
    {
        var factor = value;
        int Scale(int inner)
        {
            Func<int, int> add = x => x + factor + Leaf(x);
            return add(inner);
        }

        return Scale(2);
    }

    public static async Task<int> OverloadedAsync(int value)
    {
        await Task.Delay(1);
        return Leaf(value);
    }

    public static async Task<string> OverloadedAsync(string value)
    {
        await Task.Delay(1);
        return value + Leaf(0);
    }

    public static int OverloadedLambda(int value)
    {
        Func<int, int> pick = static x => x + Leaf(x);
        return pick(value);
    }

    public static string OverloadedLambda(string value)
    {
        Func<string, string> pick = static x => x + Leaf(0);
        return pick(value);
    }

    private static int Leaf(int value) => value + 1;
}

public sealed class Box<T>
{
    public IEnumerable<T> Each(T[] items)
    {
        foreach (var item in items)
            yield return item;
    }
}
