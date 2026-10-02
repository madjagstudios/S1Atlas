namespace S1Atlas.ParityFixture;

public sealed class SwitchCases
{
    public int Dispatch(int value)
    {
        switch (value)
        {
            case 0:
                return Target(0);
            case 1:
                goto Done;
            default:
                return Target(value);
        }

    Done:
        return Target(1);
    }

    private static int Target(int value) => value + 1;
}
