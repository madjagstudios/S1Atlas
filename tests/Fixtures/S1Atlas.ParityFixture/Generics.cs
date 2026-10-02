namespace S1Atlas.ParityFixture;

public class GenericBox<T>
{
    public T Item { get; set; } = default!;
    public T Touch(T value) => value;
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
}
