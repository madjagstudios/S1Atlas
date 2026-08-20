using S1Atlas.Docs.Identity;
using Xunit;

namespace S1Atlas.Docs.Tests.Identity;

public sealed class PortalSlugServiceTests
{
    [Fact]
    public void Create_uses_safe_readable_slug_hash_suffix_and_shard_for_exact_key()
    {
        var result = new PortalSlugService().Create("ScheduleOne.Employees.Employee.Fire(System.Int32)");

        Assert.DoesNotContain(result.ReadableSlug, character => "<>:\"/\\|?* (),`".Contains(character));
        Assert.Equal(12, result.HashSuffix.Length);
        Assert.Equal(result.HashSuffix[..2], result.HashPrefix);
        Assert.Equal(result.HashSuffix, result.HashSuffix.ToLowerInvariant());
    }

    [Fact]
    public void Create_keeps_case_only_keys_distinct_and_avoids_windows_device_names()
    {
        var service = new PortalSlugService();
        var upper = service.Create("Employee");
        var lower = service.Create("employee");
        var device = service.Create("CON");

        Assert.NotEqual(upper.HashSuffix, lower.HashSuffix);
        Assert.StartsWith("x-", device.ReadableSlug, StringComparison.Ordinal);
    }
}
