namespace S1Atlas.ParityFixture;

public sealed class DelegateCases
{
    public static int StaticTarget(int value) => value * 2;

    public int InstanceTarget(int value) => value + 1;

    public event Func<int, int>? Pinged;

    public Func<int, int> BuildStaticFunc() => StaticTarget;

    public Func<int, int> BuildInstanceFunc() => InstanceTarget;

    public int RaisePinged(int value) => Pinged?.Invoke(value) ?? value;

    public void SubscribeSelf() => Pinged += InstanceTarget;

    public void UnsubscribeSelf() => Pinged -= InstanceTarget;

    public Action BuildStaticAction() => StaticActionTarget;

    public Action BuildInstanceAction() => InstanceActionTarget;

    public static void StaticActionTarget() { }

    public void InstanceActionTarget() { }
}

public sealed class DelegateHolder
{
    public Func<int, int> Handler = DelegateCases.StaticTarget;

    public int Fire(int value)
    {
        return Handler(value);
    }
}
