namespace ScheduleOne.UI
{
    public class HUD
    {
        protected TMPro.TextMeshProUGUI topScreenText = new();

        protected UnityEngine.RectTransform topScreenText_Background = new();
    }
}

namespace TMPro
{
    public class TextMeshProUGUI
    {
        public string text { get; set; } = string.Empty;
    }
}

namespace UnityEngine
{
    public class RectTransform;
}
