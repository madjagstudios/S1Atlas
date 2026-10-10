namespace Mod;

public static class MemberAccessProbe
{
    public static int ReadWrite(Game.MemberState first, Game.OtherMemberState second)
    {
        first.Count = first.Count + 1;
        second.Count = second.Count + 2;
        Game.MemberState.SharedCount = Game.MemberState.SharedCount + 3;
        Game.OtherMemberState.SharedCount = Game.OtherMemberState.SharedCount + 4;
        first.Value = first.Value + first.Count;
        second.Value = second.Value + second.Count;

        return first.Value + second.Value + first.Count + second.Count
            + Game.MemberState.SharedCount + Game.OtherMemberState.SharedCount;
    }
}
