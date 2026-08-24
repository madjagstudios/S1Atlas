using System.Text.Json;
using S1Atlas.Cli.Performance;
using Xunit;

namespace S1Atlas.IntegrationTests.Performance;

public sealed class PerformanceMeasurementTests
{
    [Fact]
    public void CompleteReport_ContainsProcessFilesystemPhaseAndCounterMetrics()
    {
        var dataRoot = Directory.CreateTempSubdirectory("s1atlas-performance-");
        try
        {
            File.WriteAllText(Path.Combine(dataRoot.FullName, "existing.txt"), "baseline");

            using var measurement = new PerformanceMeasurement("scan", dataRoot.FullName);
            using (measurement.Measure("environment.discovery"))
            {
                File.WriteAllText(Path.Combine(dataRoot.FullName, "created.txt"), "observed");
            }

            measurement.SetCounter("dependencies.total", 4);

            var report = measurement.Complete();
            using var json = JsonDocument.Parse(measurement.ToJson(report));

            Assert.Equal("scan", report.Command);
            Assert.True(report.ElapsedMilliseconds >= 0);
            Assert.True(report.CpuMilliseconds >= 0);
            Assert.True(report.AllocatedBytes >= 0);
            Assert.True(report.WorkingSetBytesBefore > 0);
            Assert.True(report.WorkingSetBytesAfter > 0);
            Assert.Equal(1, report.FileCountBefore);
            Assert.Equal(2, report.FileCountAfter);
            Assert.Equal(8, report.BytesBefore);
            Assert.Equal(16, report.BytesAfter);
            Assert.Equal(4, report.Counters["dependencies.total"]);
            Assert.Equal("environment.discovery", Assert.Single(report.Phases).Name);
            Assert.Equal("scan", json.RootElement.GetProperty("command").GetString());
            Assert.True(json.RootElement.GetProperty("phases").GetArrayLength() == 1);
        }
        finally
        {
            dataRoot.Delete(recursive: true);
        }
    }
}
