// Exercises the shared custom-attribute decoder: constructor plus named arguments,
// including an enum, a typeof, and a Type array. Not a Harmony shape.
namespace Mod;

public enum SampleKind
{
    Alpha,
    Beta
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class FixtureCaseAttribute : Attribute
{
    public FixtureCaseAttribute(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public SampleKind Kind { get; set; }

    public Type? Target { get; set; }

    public Type[]? Types { get; set; }

    public int Count { get; set; }

    public System.AttributeTargets Targets { get; set; }
}

[FixtureCase("decoder", Kind = SampleKind.Beta, Target = typeof(Game.Widget), Types = new Type[] { typeof(int), typeof(string) }, Count = 3, Targets = System.AttributeTargets.Class)]
public class DecoderProbe
{
}
