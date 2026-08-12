using S1Atlas.Core.Environment;

namespace S1Atlas.Core.Discovery;

public interface IDependencyDetector
{
    IReadOnlyList<DependencyVersion> Detect(ScheduleOneInstallation installation);
}
