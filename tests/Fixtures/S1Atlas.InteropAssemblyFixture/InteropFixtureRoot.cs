namespace S1Atlas.InteropAssemblyFixture;

public sealed class InteropFixtureRoot
{
    public int InteropWrapper(int value) => InteropRuntime.il2cpp_runtime_invoke(value);

    public int InteropWrapperConvertArgs(int value) => InteropRuntime.il2cpp_runtime_invoke_convert_args(value);

    public int NotInteropWrapper(int value) => InteropRuntime.il2cpp_runtime_invoker(value);
}

public static class InteropRuntime
{
    public static int il2cpp_runtime_invoke(int value) => value + 1;

    public static int il2cpp_runtime_invoke_convert_args(int value) => value + 2;

    public static int il2cpp_runtime_invoker(int value) => value + 3;
}
