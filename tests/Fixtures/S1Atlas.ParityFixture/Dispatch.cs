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

public class DispatchGrandchild : DispatchDerived
{
    public override int Foo() => 7;
}

public class DispatchNewVirtual : DispatchBase
{
    public new virtual int Foo() => 8;
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

public class GenericBase<T>
{
    public virtual string Describe() => "base";
}

public class GenericDerived : GenericBase<int>
{
    public override string Describe() => "derived";
}

public class ExternalToString
{
    public override string ToString() => "external";
}

public class PropBase
{
    public virtual string Label => "base";
}

public class PropDerived : PropBase
{
    public override string Label => "derived";
}

public class EventBase
{
    public virtual event EventHandler? Changed;

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

public class EventDerived : EventBase
{
    public override event EventHandler? Changed;

    public new void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

public interface IWorker
{
    int Work();
}

public class ImplBase
{
    public int Work() => 1;
}

public class ImplDerived : ImplBase, IWorker
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
