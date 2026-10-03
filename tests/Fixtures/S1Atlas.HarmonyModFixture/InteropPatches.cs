// Interop-view stand-in: mirrors the generated Il2Cpp projection shape (Il2Cpp-prefixed
// namespace) so patch targets written against the interop view resolve to game symbols
// through the normalizer. The game index holds Game.Widget only.
using Game;
using HarmonyLib;

namespace Il2CppGame
{
    public class Widget
    {
    }

    public class Gadget
    {
    }
}

namespace Mod
{
    [HarmonyPatch(typeof(Il2CppGame.Widget), "Run")]
    public class InteropPrefixPatch
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
        }
    }

    [HarmonyPatch(typeof(Game.Widget), "Calibrate", new Type[] { typeof(Il2CppGame.Gadget) })]
    public class InteropParamPatch
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
        }
    }
}
