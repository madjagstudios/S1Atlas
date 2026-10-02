namespace S1Atlas.ParityFixture;

public interface IFoo
{
    int Foo();
}

public readonly struct FooStruct : IFoo
{
    public int Foo() => 42;
}

public sealed class ConstrainedCases
{
    public int CallThrough<T>(ref T item) where T : struct, IFoo => item.Foo();
}
