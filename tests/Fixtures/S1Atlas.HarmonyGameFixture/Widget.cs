// Fixture game surface for Harmony patch-target tests. Written from scratch; every
// member below exists to be (or deliberately not to be) a patch target.
namespace Game;

public class Gadget
{
}

public class Widget
{
    public static int Counter;

    public Widget()
    {
    }

    static Widget()
    {
    }

    public string Name { get; set; } = string.Empty;

    public void Run()
    {
    }

    public void Untouched()
    {
    }

    public void Pristine()
    {
    }

    public int Compute(int x) => x;

    public int Compute(int x, string y) => x + y.Length;

    public int Compute(ref int x) => x;

    public void Consume(int x)
    {
    }

    public void Consume(out int x)
    {
        x = 0;
    }

    public void Calibrate(Gadget gadget)
    {
    }

    public class Nested
    {
        public void Inner()
        {
        }
    }
}
