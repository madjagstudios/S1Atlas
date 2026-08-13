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
