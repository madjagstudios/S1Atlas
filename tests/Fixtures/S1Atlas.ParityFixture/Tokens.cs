namespace S1Atlas.ParityFixture;

public sealed class TokenCases
{
    private static readonly int[] Data = [1, 2, 3, 4, 5, 6, 7, 8];

    public int ReadData(int index) => Data[index];

    public string TypeName() => typeof(int).FullName!;
}
