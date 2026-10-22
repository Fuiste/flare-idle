using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace FlareIdle
{
    internal sealed class FakeGpuDriver : IGpuDriver
    {
        public int Initialized, Closed, Writes;
        public uint CountValue = 2, Cap = 225500, Minimum = 100000, Maximum = 450000;
        public bool IgnoreWrite, FailWrite, PowerUnavailable, OptionalUnavailable, BadIdentity, FailInitialize;
        public string PciValue = "00000000:2a:00.0";
        public int Initialize() { Initialized++; return FailInitialize ? 1 : 0; }
        public int Shutdown() { Closed++; return 0; }
        public string Error(int status) { return "fake driver error " + status; }
        public int Count(out uint count) { count = CountValue; return 0; }
        public int HandleByIndex(uint index, out IntPtr handle) { handle = new IntPtr(index + 1); return 0; }
        public int HandleByUuid(string uuid, out IntPtr handle) { handle = new IntPtr(1); return 0; }
        public int Name(IntPtr handle, out string value) { value = "example GPU " + handle; return 0; }
        public int Uuid(IntPtr handle, out string value) { value = BadIdentity ? "GPU-other" : "GPU-example-" + handle; return 0; }
        public int Index(IntPtr handle, out uint value) { value = (uint)handle.ToInt64() - 1; return 0; }
        public int PciBusId(IntPtr handle, out string value) { value = PciValue; return OptionalUnavailable ? 3 : 0; }
        public int ComputeCapability(IntPtr handle, out int major, out int minor) { major = 12; minor = 0; return OptionalUnavailable ? 3 : 0; }
        public int PowerUsage(IntPtr handle, out uint value) { value = 73123; return OptionalUnavailable ? 3 : 0; }
        public int PowerLimit(IntPtr handle, out uint value) { value = Cap; return PowerUnavailable ? 3 : 0; }
        public int DefaultPowerLimit(IntPtr handle, out uint value) { value = 300000; return OptionalUnavailable ? 3 : 0; }
        public int PowerConstraints(IntPtr handle, out uint min, out uint max) { min = Minimum; max = Maximum; return PowerUnavailable ? 3 : 0; }
        public int SetPowerLimit(IntPtr handle, uint value) { Writes++; if (FailWrite) return 4; if (!IgnoreWrite) Cap = value; return 0; }
        public int Temperature(IntPtr handle, out uint value) { value = 55; return OptionalUnavailable ? 3 : 0; }
        public int Memory(IntPtr handle, out ulong total, out ulong used) { total = 24UL * 1073741824UL; used = 3UL * 1073741824UL; return OptionalUnavailable ? 3 : 0; }
        public int Utilization(IntPtr handle, out uint value) { value = 20; return OptionalUnavailable ? 3 : 0; }
        public int MemoryTemperature(IntPtr handle, out double value) { value = Double.NaN; return OptionalUnavailable ? 3 : 0; }
    }

    internal static class GpuTests
    {
        static int passed;
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static void Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new Exception("expected " + typeof(T).Name);
        }
        static void Test(string name, Action action) { action(); passed++; Console.WriteLine("pass: " + name); }

        public static int Main()
        {
            try
            {
                Test("discovery returns every device and reported capability", delegate {
                    var driver = new FakeGpuDriver();
                    var devices = Gpu.Enumerate(driver);
                    Check(devices.Length == 2 && devices[1].Index == 1 && devices[1].Uuid == "GPU-example-2", "device inventory mismatch");
                    Check(devices[0].ComputeMajor == 12 && devices[0].ComputeMinor == 0, "compute capability missing");
                    Check(devices[0].PciBusId == "00000000:2a:00.0", "PCI identity missing");
                    Check(devices[0].MinPowerLimitWatts == 100 && devices[0].MaxPowerLimitWatts == 450, "hardware bounds missing");
                    Check(driver.Initialized == 1 && driver.Closed == 1 && driver.Writes == 0, "discovery changed power or leaked driver reference");
                });
                Test("observation never changes GPU settings", delegate {
                    var driver = new FakeGpuDriver();
                    using (var gpu = new Gpu("GPU-example-1", driver)) {
                        var read = gpu.Read();
                        Check(read.MemoryTotalGiB == 24 && read.MemoryUsedGiB == 3 && read.PowerWatts == 73.123, "valid telemetry mismatch");
                        Check(read.MemoryTemperatureC == null, "invalid memory temperature was exposed");
                    }
                    Check(driver.Writes == 0 && driver.Closed == 1, "observation must be read-only");
                });
                Test("restoration uses exact pre-session cap rather than factory default", delegate {
                    var driver = new FakeGpuDriver();
                    using (var gpu = new Gpu("GPU-example-1", driver)) {
                        double baseline = gpu.CaptureBaseline();
                        gpu.ApplyCap(400);
                        Check(driver.Cap == 400000, "explicit cap not applied");
                        gpu.Restore(baseline);
                        Check(driver.Cap == 225500 && driver.Writes == 2, "pre-session cap not restored exactly");
                    }
                });
                Test("explicit caps use each device's reported bounds", delegate {
                    var driver = new FakeGpuDriver { Minimum = 35000, Maximum = 65000, Cap = 50000 };
                    using (var gpu = new Gpu("GPU-example-1", driver)) {
                        Throws<ArgumentOutOfRangeException>(delegate { gpu.ApplyCap(34); });
                        Throws<ArgumentOutOfRangeException>(delegate { gpu.ApplyCap(66); });
                        Check(driver.Writes == 0, "invalid cap reached driver");
                        gpu.ApplyCap(60);
                        Check(driver.Cap == 60000, "valid lower-power device cap rejected");
                    }
                });
                Test("readback mismatch is a hard failure", delegate {
                    var driver = new FakeGpuDriver { IgnoreWrite = true };
                    using (var gpu = new Gpu("GPU-example-1", driver))
                        Throws<InvalidOperationException>(delegate { gpu.ApplyCap(400); });
                    Check(driver.Writes == 1, "readback failure must not trigger blind retries");
                });
                Test("read-only mode works without adjustable power support", delegate {
                    var driver = new FakeGpuDriver { PowerUnavailable = true, OptionalUnavailable = true };
                    using (var gpu = new Gpu("GPU-example-1", driver)) {
                        var read = gpu.Read();
                        Check(read.Name != null && read.PowerLimitWatts == null && read.TemperatureC == null && read.ComputeMajor == null, "unsupported telemetry must stay unknown");
                        Check(read.PciBusId == null, "unsupported PCI identity must stay unknown");
                        Throws<InvalidOperationException>(delegate { gpu.CaptureBaseline(); });
                        Throws<InvalidOperationException>(delegate { gpu.ApplyCap(400); });
                    }
                    Check(driver.Writes == 0, "unsupported device should never be written");
                });
                Test("identity mismatch closes driver reference without writes", delegate {
                    var driver = new FakeGpuDriver { BadIdentity = true };
                    Throws<InvalidOperationException>(delegate { new Gpu("GPU-example-1", driver); });
                    Check(driver.Closed == 1 && driver.Writes == 0, "failed identity leaked reference");
                });
                Test("invalid UUID is rejected before opening driver", delegate {
                    var driver = new FakeGpuDriver();
                    Throws<ArgumentException>(delegate { new Gpu("0", driver); });
                    Check(driver.Initialized == 0, "index selection accepted instead of exact identity");
                });
                Test("disposed device cannot read or change settings", delegate {
                    var driver = new FakeGpuDriver();
                    var gpu = new Gpu("GPU-example-1", driver); gpu.Dispose(); gpu.Dispose();
                    Throws<ObjectDisposedException>(delegate { gpu.Read(); });
                    Throws<ObjectDisposedException>(delegate { gpu.ApplyCap(200); });
                    Check(driver.Closed == 1 && driver.Writes == 0, "dispose is not idempotent");
                });
                Test("malformed restoration values never reach driver", delegate {
                    var driver = new FakeGpuDriver();
                    using (var gpu = new Gpu("GPU-example-1", driver)) {
                        foreach (double value in new[] {Double.NaN, Double.PositiveInfinity, -1, 0, 99, 451, 200.0005}) {
                            double bad = value;
                            Throws<ArgumentOutOfRangeException>(delegate { gpu.Restore(bad); });
                        }
                    }
                    Check(driver.Writes == 0, "invalid restore reached driver");
                });
                Test("driver errors do not trigger automatic restoration or retries", delegate {
                    var driver = new FakeGpuDriver { FailWrite = true };
                    using (var gpu = new Gpu("GPU-example-1", driver))
                        Throws<InvalidOperationException>(delegate { gpu.ApplyCap(300); });
                    Check(driver.Writes == 1 && driver.Cap == 225500, "failed write retried or restored on dispose");
                });
                Test("empty inventory is valid and does not write settings", delegate {
                    var driver = new FakeGpuDriver { CountValue = 0 };
                    Check(Gpu.Enumerate(driver).Length == 0 && driver.Writes == 0, "empty inventory failed");
                });
                Test("failed initialization does not release an unowned reference", delegate {
                    var driver = new FakeGpuDriver { FailInitialize = true };
                    Throws<InvalidOperationException>(delegate { Gpu.Enumerate(driver); });
                    Check(driver.Closed == 0, "unowned driver reference was released");
                });
                Test("zero pre-session cap is not accepted for restoration", delegate {
                    var driver = new FakeGpuDriver { Cap = 0 };
                    using (var gpu = new Gpu("GPU-example-1", driver))
                        Throws<InvalidOperationException>(delegate { gpu.CaptureBaseline(); });
                });
                Test("unrestorable pre-session cap is rejected before any write", delegate {
                    var driver = new FakeGpuDriver { Cap = 500000 };
                    using (var gpu = new Gpu("GPU-example-1", driver))
                        Throws<InvalidOperationException>(delegate { gpu.CaptureBaseline(); });
                    Check(driver.Writes == 0, "invalid pre-session cap must not be changed");
                });
                Test("a changed selected device identity invalidates subsequent reads", delegate {
                    var driver = new FakeGpuDriver();
                    using (var gpu = new Gpu("GPU-example-1", driver)) {
                        driver.BadIdentity = true;
                        Throws<InvalidOperationException>(delegate { gpu.Read(); });
                    }
                });
                Test("PCI identity preserves domain and normalizes case without writes", delegate {
                    var driver = new FakeGpuDriver { PciValue = "01234567:2A:1F.7" };
                    using (var gpu = new Gpu("GPU-example-1", driver))
                        Check(gpu.Read().PciBusId == "01234567:2a:1f.7", "PCI domain was changed or lost");
                    Check(driver.Writes == 0, "PCI discovery must be read-only");
                });
                Test("invalid PCI identity is unavailable instead of unsafe command text", delegate {
                    var driver = new FakeGpuDriver { PciValue = "bad --argument" };
                    using (var gpu = new Gpu("GPU-example-1", driver)) {
                        Check(gpu.Read().PciBusId == null, "invalid PCI identifier exposed");
                        driver.PciValue = "00000000:2a:ff.0";
                        Check(gpu.Read().PciBusId == null, "invalid device number exposed");
                    }
                    Check(driver.Writes == 0, "invalid PCI lookup changed settings");
                });
                Test("PCI-info v3 layout matches the native ABI", delegate {
                    Type type = typeof(NvmlDriver).GetNestedType("NvmlPciInfo", BindingFlags.NonPublic);
                    Check(Marshal.SizeOf(type) == 68, "PCI-info structure size mismatch");
                    Check(Marshal.OffsetOf(type, "Domain").ToInt32() == 16 && Marshal.OffsetOf(type, "BusId").ToInt32() == 36,
                        "PCI-info field offset mismatch");
                });
                Console.WriteLine(passed + " GPU checks passed; no physical GPU mutations performed");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
