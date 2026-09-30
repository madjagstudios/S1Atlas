using System.Runtime.InteropServices;
using System.Text;

namespace S1Atlas.Mcp.Tests;

/// <summary>
/// Reads another process's command line for exact test-server attribution.
/// 64-bit only; anything unexpected fails closed to "unknown" so a failed
/// lookup can never cause a wrongful kill.
/// </summary>
internal static class ProcessCommandLine
{
    private const int ProcessBasicInformation = 0;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessVmRead = 0x0010;

    // x64 offsets: PEB.ProcessParameters, then
    // RTL_USER_PROCESS_PARAMETERS.CommandLine (UNICODE_STRING).
    private const int PebProcessParametersOffset = 0x20;
    private const int ParametersCommandLineOffset = 0x70;

    public static bool TryGetCommandLine(int processId, out string? commandLine)
    {
        commandLine = null;
        if (!Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem)
        {
            return false;
        }

        var handle = OpenProcess(
            ProcessQueryLimitedInformation | ProcessVmRead,
            false,
            processId);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var basicInfo = default(PROCESS_BASIC_INFORMATION);
            var status = NtQueryInformationProcess(
                handle,
                ProcessBasicInformation,
                ref basicInfo,
                (uint)Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(),
                out _);
            if (status != 0 || basicInfo.PebBaseAddress == IntPtr.Zero)
            {
                return false;
            }

            var parametersAddress = ReadPointer(handle, basicInfo.PebBaseAddress + PebProcessParametersOffset);
            if (parametersAddress == IntPtr.Zero)
            {
                return false;
            }

            var lengthBytes = ReadUInt16(handle, parametersAddress + ParametersCommandLineOffset);
            var bufferAddress = ReadPointer(handle, parametersAddress + ParametersCommandLineOffset + IntPtr.Size);
            if (lengthBytes == 0 || bufferAddress == IntPtr.Zero)
            {
                return false;
            }

            var buffer = new byte[lengthBytes];
            if (!ReadProcessMemory(handle, bufferAddress, buffer, buffer.Length, out var bytesRead) ||
                bytesRead != buffer.Length)
            {
                return false;
            }

            commandLine = Encoding.Unicode.GetString(buffer);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static IntPtr ReadPointer(IntPtr processHandle, IntPtr address)
    {
        var buffer = new byte[IntPtr.Size];
        if (!ReadProcessMemory(processHandle, address, buffer, buffer.Length, out var bytesRead) ||
            bytesRead != buffer.Length)
        {
            return IntPtr.Zero;
        }

        return IntPtr.Size == 8
            ? new IntPtr(BitConverter.ToInt64(buffer, 0))
            : new IntPtr(BitConverter.ToInt32(buffer, 0));
    }

    private static ushort ReadUInt16(IntPtr processHandle, IntPtr address)
    {
        var buffer = new byte[sizeof(ushort)];
        if (!ReadProcessMemory(processHandle, address, buffer, buffer.Length, out var bytesRead) ||
            bytesRead != buffer.Length)
        {
            return 0;
        }

        return BitConverter.ToUInt16(buffer, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2a;
        public IntPtr Reserved2b;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation,
        uint processInformationLength,
        out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        byte[] buffer,
        int size,
        out int bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
