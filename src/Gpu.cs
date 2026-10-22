using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace FlareIdle
{
    public sealed class GpuReading
    {
        public int Index = -1;
        public string Name;
        public string Uuid;
        public string PciBusId;
        public int? ComputeMajor;
        public int? ComputeMinor;
        public double? PowerWatts;
        public double? PowerLimitWatts;
        public double? DefaultPowerLimitWatts;
        public double? MinPowerLimitWatts;
        public double? MaxPowerLimitWatts;
        public double? TemperatureC;
        public double? MemoryUsedGiB;
        public double? MemoryTotalGiB;
        public double? UtilizationPercent;
        public double? MemoryTemperatureC;
    }

    // The controller owns the power lease and must stop its workers before
    // restoring it. This class never changes clocks, voltage, or fan settings,
    // and disposing a read-only probe never changes the GPU.
    internal sealed class Gpu : IDisposable
    {
        readonly object sync = new object();
        readonly IGpuDriver driver;
        IntPtr device;
        bool initialized;
        bool disposed;
        string uuid;

        public Gpu(string uuid) : this(uuid, new NvmlDriver()) { }

        internal Gpu(string expectedUuid, IGpuDriver driver)
        {
            if (String.IsNullOrWhiteSpace(expectedUuid) ||
                !expectedUuid.StartsWith("GPU-", StringComparison.Ordinal))
                throw new ArgumentException("choose an exact GPU UUID from device discovery", "expectedUuid");
            if (driver == null) throw new ArgumentNullException("driver");
            this.driver = driver;
            try
            {
                Check(driver.Initialize(), "initialize NVIDIA telemetry");
                initialized = true;
                Check(driver.HandleByUuid(expectedUuid, out device), "find selected GPU");
                if (device == IntPtr.Zero) throw new InvalidOperationException("selected GPU handle is empty");
                string actual;
                Check(driver.Uuid(device, out actual), "read selected GPU UUID");
                if (!String.Equals(actual, expectedUuid, StringComparison.Ordinal))
                    throw new InvalidOperationException("selected GPU identity changed");
                uuid = actual;
            }
            catch
            {
                if (initialized) { driver.Shutdown(); initialized = false; }
                throw;
            }
        }

        public static GpuReading[] Enumerate() { return Enumerate(new NvmlDriver()); }

        internal static GpuReading[] Enumerate(IGpuDriver driver)
        {
            bool opened = false;
            try
            {
                int status = driver.Initialize();
                if (status != 0) throw Failure(driver, status, "initialize NVIDIA telemetry");
                opened = true;
                uint count;
                status = driver.Count(out count);
                if (status != 0) throw Failure(driver, status, "enumerate NVIDIA GPUs");
                if (count > 128) throw new InvalidOperationException("invalid GPU count returned by NVIDIA telemetry");
                var result = new List<GpuReading>();
                for (uint index = 0; index < count; index++)
                {
                    IntPtr handle;
                    status = driver.HandleByIndex(index, out handle);
                    if (status != 0) throw Failure(driver, status, "read GPU at index " + index);
                    if (handle == IntPtr.Zero) throw new InvalidOperationException("GPU handle is empty");
                    result.Add(ReadDevice(driver, handle));
                }
                return result.ToArray();
            }
            finally { if (opened) driver.Shutdown(); }
        }

        public GpuReading Read()
        {
            lock (sync)
            {
                EnsureOpen();
                var reading = ReadDevice(driver, device);
                if (!String.Equals(reading.Uuid, uuid, StringComparison.Ordinal))
                    throw new InvalidOperationException("selected GPU identity changed");
                return reading;
            }
        }

        // Persist this value in the controller's recovery journal before the
        // first write. It is the user's actual pre-session cap, not a factory cap.
        public double CaptureBaseline()
        {
            lock (sync)
            {
                EnsureOpen();
                uint value;
                Check(driver.PowerLimit(device, out value), "read pre-session power limit");
                if (value == 0) throw new InvalidOperationException("pre-session power limit is unavailable");
                uint minimum, maximum;
                Check(driver.PowerConstraints(device, out minimum, out maximum), "read supported power limits");
                if (minimum == 0 || maximum < minimum || value < minimum || value > maximum)
                    throw new InvalidOperationException("pre-session power limit is outside the supported restore range; disable power management");
                return value / 1000.0;
            }
        }

        public void ApplyCap(int watts)
        {
            if (watts <= 0) throw new ArgumentOutOfRangeException("watts", "power limit must be positive");
            SetAndVerify(checked((uint)watts * 1000), "set selected power limit");
        }

        public void Restore(double watts)
        {
            if (Double.IsNaN(watts) || Double.IsInfinity(watts) || watts <= 0 || watts > UInt32.MaxValue / 1000.0)
                throw new ArgumentOutOfRangeException("watts", "saved power limit is invalid");
            // A JSON double round-trip can move the exact milliW value by an
            // insignificant fraction. Never round down to an adjacent setting.
            double value = watts * 1000.0;
            if (Math.Abs(value - Math.Round(value)) > 0.001)
                throw new ArgumentOutOfRangeException("watts", "saved power limit must use whole milliwatts");
            SetAndVerify(checked((uint)Math.Round(value)), "restore pre-session power limit");
        }

        void SetAndVerify(uint value, string operation)
        {
            lock (sync)
            {
                EnsureOpen();
                uint minimum, maximum;
                Check(driver.PowerConstraints(device, out minimum, out maximum), "read supported power limits");
                if (minimum == 0 || maximum < minimum)
                    throw new InvalidOperationException("GPU does not expose valid adjustable power limits; disable power management");
                if (value < minimum || value > maximum)
                    throw new ArgumentOutOfRangeException("watts", String.Format(
                        "power limit must be within this GPU's supported range ({0:0.###}-{1:0.###} W)", minimum / 1000.0, maximum / 1000.0));
                Check(driver.SetPowerLimit(device, value), operation);
                uint actual;
                Check(driver.PowerLimit(device, out actual), "verify power limit");
                if (actual != value) throw new InvalidOperationException("GPU power limit readback does not match the requested setting");
            }
        }

        static GpuReading ReadDevice(IGpuDriver driver, IntPtr handle)
        {
            var result = new GpuReading();
            string value;
            int status = driver.Name(handle, out value);
            if (status != 0) throw Failure(driver, status, "read GPU name");
            result.Name = value;
            status = driver.Uuid(handle, out value);
            if (status != 0) throw Failure(driver, status, "read GPU UUID");
            result.Uuid = value;
            if (driver.PciBusId(handle, out value) == 0 && value != null &&
                Regex.IsMatch(value, @"\A(?:[0-9a-fA-F]{4,8}:)?[0-9a-fA-F]{2}:[01][0-9a-fA-F]\.[0-7]\z"))
                result.PciBusId = value.ToLowerInvariant();
            int major, minor;
            if (driver.ComputeCapability(handle, out major, out minor) == 0 && major > 0 && major <= 99 && minor >= 0 && minor <= 99)
            {
                result.ComputeMajor = major;
                result.ComputeMinor = minor;
            }
            uint number;
            if (driver.Index(handle, out number) == 0 && number <= Int32.MaxValue) result.Index = (int)number;
            if (driver.PowerUsage(handle, out number) == 0) result.PowerWatts = number / 1000.0;
            if (driver.PowerLimit(handle, out number) == 0 && number > 0) result.PowerLimitWatts = number / 1000.0;
            if (driver.DefaultPowerLimit(handle, out number) == 0 && number > 0) result.DefaultPowerLimitWatts = number / 1000.0;
            uint minimum, maximum;
            if (driver.PowerConstraints(handle, out minimum, out maximum) == 0 && minimum > 0 && maximum >= minimum)
            {
                result.MinPowerLimitWatts = minimum / 1000.0;
                result.MaxPowerLimitWatts = maximum / 1000.0;
            }
            if (driver.Temperature(handle, out number) == 0 && number <= 125) result.TemperatureC = number;
            ulong total, used;
            if (driver.Memory(handle, out total, out used) == 0 && total > 0 && used <= total)
            {
                result.MemoryUsedGiB = used / 1073741824.0;
                result.MemoryTotalGiB = total / 1073741824.0;
            }
            if (driver.Utilization(handle, out number) == 0 && number <= 100) result.UtilizationPercent = number;
            double memoryTemperature;
            if (driver.MemoryTemperature(handle, out memoryTemperature) == 0 &&
                !Double.IsNaN(memoryTemperature) && !Double.IsInfinity(memoryTemperature) && memoryTemperature >= 0 && memoryTemperature <= 150)
                result.MemoryTemperatureC = memoryTemperature;
            return result;
        }

        static Exception Failure(IGpuDriver driver, int status, string operation)
        {
            string detail = driver.Error(status);
            return new InvalidOperationException(operation + ": " + (detail ?? ("NVML error " + status)));
        }
        void Check(int status, string operation) { if (status != 0) throw Failure(driver, status, operation); }
        void EnsureOpen() { if (disposed || !initialized) throw new ObjectDisposedException("Gpu"); }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                if (initialized) { driver.Shutdown(); initialized = false; }
                device = IntPtr.Zero;
            }
        }
    }

    internal interface IGpuDriver
    {
        int Initialize();
        int Shutdown();
        string Error(int status);
        int Count(out uint count);
        int HandleByIndex(uint index, out IntPtr handle);
        int HandleByUuid(string uuid, out IntPtr handle);
        int Name(IntPtr handle, out string value);
        int Uuid(IntPtr handle, out string value);
        int Index(IntPtr handle, out uint value);
        int PciBusId(IntPtr handle, out string value);
        int ComputeCapability(IntPtr handle, out int major, out int minor);
        int PowerUsage(IntPtr handle, out uint value);
        int PowerLimit(IntPtr handle, out uint value);
        int DefaultPowerLimit(IntPtr handle, out uint value);
        int PowerConstraints(IntPtr handle, out uint minimum, out uint maximum);
        int SetPowerLimit(IntPtr handle, uint value);
        int Temperature(IntPtr handle, out uint value);
        int Memory(IntPtr handle, out ulong total, out ulong used);
        int Utilization(IntPtr handle, out uint value);
        int MemoryTemperature(IntPtr handle, out double value);
    }

    internal sealed class NvmlDriver : IGpuDriver
    {
        public int Initialize() { return nvmlInit_v2(); }
        public int Shutdown() { return nvmlShutdown(); }
        public string Error(int status) { IntPtr value = nvmlErrorString(status); return value == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(value); }
        public int Count(out uint count) { return nvmlDeviceGetCount_v2(out count); }
        public int HandleByIndex(uint index, out IntPtr handle) { return nvmlDeviceGetHandleByIndex_v2(index, out handle); }
        public int HandleByUuid(string uuid, out IntPtr handle) { return nvmlDeviceGetHandleByUUID(uuid, out handle); }
        public int Name(IntPtr handle, out string value) { var text = new StringBuilder(128); int status = nvmlDeviceGetName(handle, text, (uint)text.Capacity); value = text.ToString(); return status; }
        public int Uuid(IntPtr handle, out string value) { var text = new StringBuilder(128); int status = nvmlDeviceGetUUID(handle, text, (uint)text.Capacity); value = text.ToString(); return status; }
        public int Index(IntPtr handle, out uint value) { return nvmlDeviceGetIndex(handle, out value); }
        public int PciBusId(IntPtr handle, out string value)
        {
            value = null;
            try
            {
                NvmlPciInfo info;
                int status = nvmlDeviceGetPciInfo_v3(handle, out info);
                if (status == 0) value = String.IsNullOrEmpty(info.BusId) ? info.LegacyBusId : info.BusId;
                return status;
            }
            catch (EntryPointNotFoundException) { return 3; }
        }
        public int ComputeCapability(IntPtr handle, out int major, out int minor)
        {
            major = 0; minor = 0;
            try { return nvmlDeviceGetCudaComputeCapability(handle, out major, out minor); }
            catch (EntryPointNotFoundException) { return 3; }
        }
        public int PowerUsage(IntPtr handle, out uint value) { return nvmlDeviceGetPowerUsage(handle, out value); }
        public int PowerLimit(IntPtr handle, out uint value) { return nvmlDeviceGetPowerManagementLimit(handle, out value); }
        public int DefaultPowerLimit(IntPtr handle, out uint value) { return nvmlDeviceGetPowerManagementDefaultLimit(handle, out value); }
        public int PowerConstraints(IntPtr handle, out uint minimum, out uint maximum) { return nvmlDeviceGetPowerManagementLimitConstraints(handle, out minimum, out maximum); }
        public int SetPowerLimit(IntPtr handle, uint value) { return nvmlDeviceSetPowerManagementLimit(handle, value); }
        public int Temperature(IntPtr handle, out uint value) { return nvmlDeviceGetTemperature(handle, 0, out value); }
        public int Memory(IntPtr handle, out ulong total, out ulong used) { NvmlMemory value; int status = nvmlDeviceGetMemoryInfo(handle, out value); total = value.Total; used = value.Used; return status; }
        public int Utilization(IntPtr handle, out uint value) { NvmlUtilization sample; int status = nvmlDeviceGetUtilizationRates(handle, out sample); value = sample.Gpu; return status; }
        public int MemoryTemperature(IntPtr handle, out double value)
        {
            value = 0;
            try
            {
                var sample = new NvmlFieldValue { FieldId = 82 };
                int status = nvmlDeviceGetFieldValues(handle, 1, ref sample);
                if (status != 0) return status;
                if (sample.NvmlReturn != 0) return sample.NvmlReturn;
                switch (sample.ValueType)
                {
                    case 0: value = sample.Value.Double; return 0;
                    case 1: case 2: value = sample.Value.UInt; return 0;
                    case 3: value = sample.Value.ULongLong; return 0;
                    case 4: value = sample.Value.LongLong; return 0;
                    default: return 3;
                }
            }
            catch (EntryPointNotFoundException) { return 3; }
        }

        [StructLayout(LayoutKind.Sequential)] struct NvmlMemory { public ulong Total, Free, Used; }
        [StructLayout(LayoutKind.Sequential)] struct NvmlUtilization { public uint Gpu, Memory; }
        // NVML's v3 PCI-info ABI: 16-byte legacy BDF, five uints, 32-byte BDF.
        // Keep the domain in the returned identifier; dropping it can select a
        // different device on hosts with multiple PCI domains.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)] struct NvmlPciInfo
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string LegacyBusId;
            public uint Domain, Bus, Device, DeviceId, SubSystemId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string BusId;
        }
        [StructLayout(LayoutKind.Explicit, Size = 8)] struct NvmlValue
        {
            [FieldOffset(0)] public double Double;
            [FieldOffset(0)] public uint UInt;
            [FieldOffset(0)] public ulong ULongLong;
            [FieldOffset(0)] public long LongLong;
        }
        [StructLayout(LayoutKind.Sequential)] struct NvmlFieldValue
        {
            public uint FieldId, ScopeId;
            public long Timestamp, LatencyUsec;
            public int ValueType, NvmlReturn;
            public NvmlValue Value;
        }

        // The NVIDIA driver installs NVML in System32. Never load a DLL from a
        // writable app/current directory, especially in the elevated controller.
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlInit_v2();
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlShutdown();
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr nvmlErrorString(int result);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetCount_v2(out uint count);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] static extern int nvmlDeviceGetHandleByUUID(string uuid, out IntPtr device);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] static extern int nvmlDeviceGetUUID(IntPtr device, StringBuilder uuid, uint length);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetIndex(IntPtr device, out uint value);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetPciInfo_v3(IntPtr device, out NvmlPciInfo value);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetCudaComputeCapability(IntPtr device, out int major, out int minor);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetPowerManagementDefaultLimit(IntPtr device, out uint value);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint value);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetPowerManagementLimitConstraints(IntPtr device, out uint minimum, out uint maximum);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceSetPowerManagementLimit(IntPtr device, uint value);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint value);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetTemperature(IntPtr device, uint sensorType, out uint value);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization value);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetFieldValues(IntPtr device, int count, ref NvmlFieldValue value);
    }
}
