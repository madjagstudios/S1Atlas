using System.Diagnostics;

namespace S1Atlas.Web;

public interface IBrowserLauncher
{
    void Launch(Uri url);
}

public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public void Launch(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        using var _ = Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
    }
}
