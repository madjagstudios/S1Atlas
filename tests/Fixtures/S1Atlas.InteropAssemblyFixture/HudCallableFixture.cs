namespace Il2CppScheduleOne.UI
{
    public class HUD
    {
        public static HUD Instance { get; } = new();

        public Il2CppTMPro.TextMeshProUGUI topScreenText { get; set; } = new();

        public Il2CppUnityEngine.RectTransform topScreenText_Background { get; set; } = new();
    }
}

namespace Il2CppTMPro
{
    public class TextMeshProUGUI
    {
        public string text { get; set; } = string.Empty;
    }
}

namespace Il2CppUnityEngine
{
    public class RectTransform;
}
