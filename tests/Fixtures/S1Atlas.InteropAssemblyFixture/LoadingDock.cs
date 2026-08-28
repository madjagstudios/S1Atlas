namespace S1Atlas.ManagedAssemblyFixture;

public sealed class LoadingDock
{
    public void SetOccupant(string occupant) => _ = S1Atlas.InteropAssemblyFixture.InteropRuntime.il2cpp_runtime_invoke(occupant.Length);

    internal void ResetOccupant() => _ = S1Atlas.InteropAssemblyFixture.InteropRuntime.il2cpp_runtime_invoke(0);

    public int X_k__BackingField;
}
