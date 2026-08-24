using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace S1Atlas.Cli.Performance;

internal sealed record PerformancePhase(
    string Name,
    long ElapsedMilliseconds);

internal sealed record PerformanceReport(
    string Command,
    DateTimeOffset StartedAtUtc,
    long ElapsedMilliseconds,
    long CpuMilliseconds,
    long AllocatedBytes,
    long WorkingSetBytesBefore,
    long WorkingSetBytesAfter,
    long PeakWorkingSetBytes,
    int FileCountBefore,
    int FileCountAfter,
    long BytesBefore,
    long BytesAfter,
    IReadOnlyList<PerformancePhase> Phases,
    IReadOnlyDictionary<string, long> Counters);

internal sealed class PerformanceMeasurement : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Process _process;
    private readonly string _dataRoot;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly TimeSpan _startingCpu;
    private readonly long _startingAllocatedBytes;
    private readonly FileSystemMeasurement _startingFiles;
    private readonly List<PerformancePhase> _phases = [];
    private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);
    private long _peakWorkingSetBytes;
    private PerformanceReport? _completedReport;

    public PerformanceMeasurement(string command, string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        Command = command;
        StartedAtUtc = DateTimeOffset.UtcNow;
        _dataRoot = Path.GetFullPath(dataRoot);
        _process = Process.GetCurrentProcess();
        _startingCpu = _process.TotalProcessorTime;
        _startingAllocatedBytes = GC.GetTotalAllocatedBytes(true);
        _startingFiles = MeasureFiles();
        _peakWorkingSetBytes = ObserveWorkingSet();
    }

    public string Command { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public IDisposable Measure(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new PhaseScope(this, name);
    }

    public void SetCounter(string name, long value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        _counters[name] = value;
    }

    public PerformanceReport Complete()
    {
        if (_completedReport is not null)
        {
            return _completedReport;
        }

        var files = MeasureFiles();
        var workingSet = ObserveWorkingSet();
        var cpuMilliseconds = Math.Max(
            0,
            (long)(_process.TotalProcessorTime - _startingCpu).TotalMilliseconds);
        var allocatedBytes = Math.Max(
            0,
            GC.GetTotalAllocatedBytes(true) - _startingAllocatedBytes);

        _completedReport = new PerformanceReport(
            Command,
            StartedAtUtc,
            Math.Max(0, _stopwatch.ElapsedMilliseconds),
            cpuMilliseconds,
            allocatedBytes,
            _startingFiles.WorkingSetBytes,
            workingSet,
            _peakWorkingSetBytes,
            _startingFiles.FileCount,
            files.FileCount,
            _startingFiles.Bytes,
            files.Bytes,
            _phases.ToArray(),
            new Dictionary<string, long>(_counters, StringComparer.Ordinal));
        return _completedReport;
    }

    public string ToJson(PerformanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    public void Dispose() => _stopwatch.Stop();

    private FileSystemMeasurement MeasureFiles()
    {
        if (!Directory.Exists(_dataRoot))
        {
            return new FileSystemMeasurement(0, 0, ObserveWorkingSet());
        }

        var fileCount = 0;
        long bytes = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                _dataRoot,
                "*",
                new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }))
            {
                try
                {
                    bytes += new FileInfo(path).Length;
                    fileCount++;
                }
                catch (IOException)
                {
                    // A concurrent file disappearing is not a reason to fail the command.
                }
                catch (UnauthorizedAccessException)
                {
                    // Inaccessible files are excluded from observational diagnostics.
                }
            }
        }
        catch (IOException)
        {
            // The data root can be changing while extraction or indexing is active.
        }
        catch (UnauthorizedAccessException)
        {
            // The command's result must not depend on diagnostic read permissions.
        }

        return new FileSystemMeasurement(fileCount, bytes, ObserveWorkingSet());
    }

    private long ObserveWorkingSet()
    {
        _process.Refresh();
        _peakWorkingSetBytes = Math.Max(_peakWorkingSetBytes, _process.WorkingSet64);
        return _process.WorkingSet64;
    }

    private sealed class PhaseScope(PerformanceMeasurement owner, string name) : IDisposable
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly PerformanceMeasurement _owner = owner;
        private readonly string _name = name;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopwatch.Stop();
            _owner.ObserveWorkingSet();
            _owner._phases.Add(new PerformancePhase(
                _name,
                Math.Max(0, _stopwatch.ElapsedMilliseconds)));
        }
    }

    private readonly record struct FileSystemMeasurement(
        int FileCount,
        long Bytes,
        long WorkingSetBytes);
}
