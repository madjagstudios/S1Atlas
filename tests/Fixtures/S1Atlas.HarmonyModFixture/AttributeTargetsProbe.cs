// Exercises the managed attribute reader: one marker attribute applied to every
// supported target kind, plus an injected-analysis attribute that the reader drops.
namespace Mod
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public sealed class ProbeMarkerAttribute : Attribute
    {
        public ProbeMarkerAttribute(string tag)
        {
            Tag = tag;
        }

        public string Tag { get; }
    }

    [ProbeMarker("type")]
    public class AttributeTargetsProbe
    {
        [ProbeMarker("field")]
        public int MarkedField;

        [ProbeMarker("property")]
        public int MarkedProperty { get; set; }

        [ProbeMarker("event")]
        public event EventHandler? MarkedEvent;

        [ProbeMarker("method")]
        public void MarkedMethod([ProbeMarker("parameter")] int markedParameter)
        {
            MarkedEvent?.Invoke(this, EventArgs.Empty);
        }

        [ProbeMarker("kept")]
        [Cpp2ILInjected.Address]
        public void FilteredMethod()
        {
        }

        public void UnmarkedMethod()
        {
        }
    }
}

namespace Cpp2ILInjected
{
    [AttributeUsage(AttributeTargets.All)]
    public sealed class AddressAttribute : Attribute
    {
    }
}
