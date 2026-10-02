namespace S1Atlas.ParityFixture;

public sealed class MemberCases
{
    public static int ConstructedCount;

    public int ValueField = 7;

    public int AutoProperty { get; set; } = 11;

    private int _backing;

    public int ManualProperty
    {
        get => _backing;
        set => _backing = value;
    }

    public event Action? Changed;

    static MemberCases()
    {
        ConstructedCount = 1;
    }

    public MemberCases()
    {
        ValueField = 8;
    }

    public MemberCases(int seed) : this()
    {
        AutoProperty = seed;
    }

    public void Touch()
    {
        Changed?.Invoke();
    }

    public static void OnChanged() { }

    public void SubscribeChanged() => Changed += OnChanged;

    public void UnsubscribeChanged() => Changed -= OnChanged;
}
