using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CaptionOverlay.Core.Diagnostics;

public sealed record GpuInfo(string Name, string Vendor, long DedicatedMemoryBytes, bool IsLikelyDiscrete);

public sealed record HardwareSummary(
    int PhysicalCores,
    int LogicalProcessors,
    long TotalMemoryBytes,
    IReadOnlyList<GpuInfo> Gpus)
{
    public GpuInfo? BestGpu => Gpus.OrderByDescending(g => g.IsLikelyDiscrete).ThenByDescending(g => g.DedicatedMemoryBytes).FirstOrDefault();

    public override string ToString()
    {
        var gpus = Gpus.Count == 0 ? "none detected" : string.Join("; ", Gpus.Select(g => $"{g.Name} ({g.DedicatedMemoryBytes / (1024 * 1024)} MB)"));
        return $"CPU: {PhysicalCores} cores / {LogicalProcessors} threads, RAM: {TotalMemoryBytes / (1024 * 1024 * 1024.0):F1} GB, GPU: {gpus}";
    }
}

public static partial class HardwareInfo
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static readonly Lazy<int> PhysicalCores = new(QueryPhysicalCores);

    public static int PhysicalCoreCount => PhysicalCores.Value;

    public static HardwareSummary Query() => new(PhysicalCoreCount, Environment.ProcessorCount, QueryTotalMemory(), QueryGpus());

    private static int QueryPhysicalCores()
    {
        try
        {
            const int relationProcessorCore = 0;
            uint length = 0;
            GetLogicalProcessorInformationEx(relationProcessorCore, IntPtr.Zero, ref length);
            if (length == 0)
            {
                return Environment.ProcessorCount;
            }
            IntPtr buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetLogicalProcessorInformationEx(relationProcessorCore, buffer, ref length))
                {
                    return Environment.ProcessorCount;
                }
                int cores = 0;
                int offset = 0;
                while (offset < length)
                {
                    // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: DWORD Relationship; DWORD Size; ...
                    int size = Marshal.ReadInt32(buffer, offset + 4);
                    cores++;
                    offset += size;
                }
                return Math.Max(1, cores);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception)
        {
            return Environment.ProcessorCount;
        }
    }

    private static long QueryTotalMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.TotalPhys : 0;
    }

    private static List<GpuInfo> QueryGpus()
    {
        var result = new List<GpuInfo>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (root is null)
            {
                return result;
            }
            foreach (var name in root.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                using var key = root.OpenSubKey(name);
                if (key?.GetValue("DriverDesc") is not string desc)
                {
                    continue;
                }
                string deviceId = key.GetValue("MatchingDeviceId") as string ?? "";
                if (!deviceId.StartsWith("pci", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // skip virtual/remote display adapters
                }
                long memory = key.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                    int i => (uint)i,
                    _ => key.GetValue("HardwareInformation.MemorySize") switch
                    {
                        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                        int i => (uint)i,
                        _ => 0,
                    },
                };
                string vendor = deviceId.Contains("ven_10de", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
                    : deviceId.Contains("ven_1002", StringComparison.OrdinalIgnoreCase) ? "AMD"
                    : deviceId.Contains("ven_8086", StringComparison.OrdinalIgnoreCase) ? "Intel"
                    : "Other";
                bool discrete = vendor is "NVIDIA" or "AMD"
                    ? memory >= 1L << 30
                    : desc.Contains("Arc", StringComparison.OrdinalIgnoreCase) && !desc.Contains("Graphics", StringComparison.OrdinalIgnoreCase);
                result.Add(new GpuInfo(desc, vendor, memory, discrete));
            }
        }
        catch (Exception)
        {
            // Hardware hints are best effort.
        }
        return result;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}
