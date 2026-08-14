namespace S1Atlas.ManagedAssemblyFixture;

/// <summary>
/// A minimal, source-built managed type compiled to an assembly named
/// "Assembly-CSharp", used only as a real, well-formed managed-metadata fixture
/// for Phase 4 tests. It is never committed as a compiled DLL; tests copy
/// <c>typeof(FixtureRoot).Assembly.Location</c> into temporary candidate trees
/// at runtime.
/// </summary>
public sealed class FixtureRoot
{
    public event EventHandler? Signal;

    public int Counter { get; set; }

    public int GetValue() => Counter;

    public void RaiseSignal() => Signal?.Invoke(this, EventArgs.Empty);
}

public interface IFixtureContract
{
    int ContractValue { get; }
}

public abstract class AbstractFixture
{
    public abstract int MissingBody(int value);
}

public class FixtureBase
{
    protected int BaseField;

    public virtual int BaseMethod(int value) => value + 1;
}

public sealed class GenericContainer<T>
{
    public T Value { get; set; } = default!;
}

public sealed class DerivedFixture : FixtureBase, IFixtureContract
{
    public int PublicField;

    public event EventHandler? Changed;

    public int ContractValue { get; private set; }

    public DerivedFixture()
        : this(0)
    {
    }

    public DerivedFixture(int initial)
    {
        PublicField = initial;
        ContractValue = initial;
    }

    public int Overload(int value) => value + PublicField;

    public string Overload(string value) => value + PublicField;

    public T GenericMethod<T>(T value) => value;

    public int ArithmeticOnly(int value)
    {
        var doubled = value * 2;
        return doubled + 1;
    }

    public int TrivialZero() => 0;

    public int ThrowStyleStub()
    {
        throw new NotSupportedException("Cpp2IL stub fixture");
    }

    public DerivedFixture BuildAndTouch(int value)
    {
        PublicField = value;
        var child = new DerivedFixture(value);
        _ = child.Overload(value);
        var current = PublicField;
        BaseField = current;
        Changed?.Invoke(this, EventArgs.Empty);
        return child;
    }
}
