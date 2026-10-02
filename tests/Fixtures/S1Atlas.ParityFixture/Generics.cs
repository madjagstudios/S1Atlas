namespace S1Atlas.ParityFixture;

public class GenericBox<T>
{
    public T Item { get; set; } = default!;
    public T Stored = default!;
    public T Touch(T value) => value;
    public U Convert<U>(U fallback) => fallback;
}

public class GenericOuter<T>
{
    public class GenericInner<U>
    {
        public string Describe(T first, U second) => $"{first}:{second}";
    }
}

public static class GenericHelpers
{
    public static T Echo<T>(T value) => value;
}

public static class GenericDrivers
{
    public static int ViaConstructedType()
    {
        var box = new GenericBox<int> { Item = 1 };
        return box.Touch(2);
    }

    public static string ViaGenericMethod() => GenericHelpers.Echo("x");

    public static int ViaOpenTouch<T>(GenericBox<T> box, T value) => 0;

    public static int UseStoredField()
    {
        var box = new GenericBox<int> { Stored = 3 };
        return box.Stored;
    }

    public static string UseNested() => new GenericOuter<int>.GenericInner<string>().Describe(1, "x");

    public static string UseConvert() => new GenericBox<int>().Convert<string>("x");

    public static int UseMultiDimArray()
    {
        var cells = new int[2, 2];
        cells[1, 1] = 2;
        return cells[1, 1];
    }

    public static void UseList()
    {
        var items = new List<int>();
        items.Add(1);
    }
}
