namespace S1Atlas.ParityFixture;

public class DispatchBase
{
    public virtual int Foo() => 1;
    public int Bar() => 2;
}

public class DispatchDerived : DispatchBase
{
    public override int Foo() => 3;
    public int CallBase() => base.Foo();
}

public abstract class DispatchAbstract
{
    public abstract int Compute(int value);
}

public class DispatchConcrete : DispatchAbstract
{
    public override int Compute(int value) => value * 2;
}

public class DispatchHider : DispatchBase
{
    public new int Bar() => 4;
}

public interface IDispatchContract
{
    int Serve();
}

public class DispatchImplicit : IDispatchContract
{
    public int Serve() => 5;
}

public class DispatchExplicit : IDispatchContract
{
    int IDispatchContract.Serve() => 6;
}

public class DispatchInherited : DispatchImplicit
{
}

public static class DispatchDrivers
{
    public static int ViaBase(DispatchBase value) => value.Foo();
    public static int ViaDerived(DispatchDerived value) => value.Foo();
    public static int ViaBaseBar(DispatchBase value) => value.Bar();
    public static int ViaHider(DispatchHider value) => value.Bar();
    public static int ViaAbstract(DispatchAbstract value) => value.Compute(1);
    public static int ViaConcrete(DispatchConcrete value) => value.Compute(2);
    public static int ViaInterface(IDispatchContract value) => value.Serve();
    public static int ViaImplicit(DispatchImplicit value) => value.Serve();
    public static int ViaInherited(DispatchInherited value) => value.Serve();
}
