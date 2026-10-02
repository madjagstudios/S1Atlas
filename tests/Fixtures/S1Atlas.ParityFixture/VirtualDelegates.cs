namespace S1Atlas.ParityFixture;

public class VirtualBase
{
    public virtual int Compute(int value) => value;
}

public sealed class VirtualDelegates
{
    public Func<int, int> BuildVirtualFunc(VirtualBase receiver) => receiver.Compute;
}
