using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace FlareIdle {
    internal static class Native {
        internal const uint CREATE_SUSPENDED = 4, CREATE_NEW_CONSOLE = 16, CREATE_NEW_PROCESS_GROUP = 512;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo {
            public int cb; public string reserved, desktop, title;
            public int x, y, width, height, countX, countY, fill, flags;
            public short show; public short reserved2; public IntPtr reservedPtr, input, output, error;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInfo { public IntPtr process, thread; public uint pid, tid; }
        [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { public int length; public IntPtr descriptor; [MarshalAs(UnmanagedType.Bool)] public bool inherit; }
        [StructLayout(LayoutKind.Sequential)] internal struct BasicLimit { public long processTime, jobTime; public uint flags; public UIntPtr min, max; public uint active; public UIntPtr affinity; public uint priority, scheduling; }
        [StructLayout(LayoutKind.Sequential)] internal struct IoCounters { public ulong a,b,c,d,e,f; }
        [StructLayout(LayoutKind.Sequential)] internal struct ExtendedLimit { public BasicLimit basic; public IoCounters io; public UIntPtr processMemory, jobMemory, peakProcess, peakJob; }
        [StructLayout(LayoutKind.Sequential)] internal struct Accounting { public long a,b,c,d; public uint faults, total, active, terminated; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref StartupInfo si, out ProcessInfo pi);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetInformationJobObject(IntPtr job, int cls, ref ExtendedLimit info, int length);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool QueryInformationJobObject(IntPtr job, int cls, out Accounting info, int length, IntPtr returned);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool TerminateJobObject(IntPtr job, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        [DllImport("kernel32.dll")] internal static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GenerateConsoleCtrlEvent(uint evt, uint group);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateFile(string name, uint access, uint share, ref SecurityAttributes attrs, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes attrs, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern int SetCurrentProcessExplicitAppUserModelID(string id);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool AllowSetForegroundWindow(uint pid);
        internal static Exception Error() { return new Win32Exception(Marshal.GetLastWin32Error()); }
        public static string Quote(string s) {
            if (s == null) s = "";
            var b = new StringBuilder("\""); int slashes = 0;
            foreach (char c in s) {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { b.Append('\\', slashes * 2 + 1); b.Append(c); }
                else { b.Append('\\', slashes); b.Append(c); }
                slashes = 0;
            }
            b.Append('\\', slashes * 2); return b.Append('"').ToString();
        }
        internal static NativeChild Create(string exe, string[] args, uint flags, bool nullStreams) {
            return Create(exe, args, flags, nullStreams, IntPtr.Zero);
        }
        internal static NativeChild Create(string exe, string[] args, uint flags, bool nullStreams, IntPtr capturedOutput) {
            var si = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)), flags = 1, show = 0 };
            IntPtr read = IntPtr.Zero, write = IntPtr.Zero;
            try {
                if (nullStreams) {
                    var sa = new SecurityAttributes { length = Marshal.SizeOf(typeof(SecurityAttributes)), inherit = true };
                    read = CreateFile("NUL", 0x80000000, 3, ref sa, 3, 0, IntPtr.Zero);
                    write = capturedOutput == IntPtr.Zero ? CreateFile("NUL", 0x40000000, 3, ref sa, 3, 0, IntPtr.Zero) : capturedOutput;
                    if (read == new IntPtr(-1) || write == new IntPtr(-1)) throw Error();
                    si.flags |= 0x100; si.input = read; si.output = si.error = write;
                }
                var b = new StringBuilder(Quote(exe)); foreach (string a in args) b.Append(" ").Append(Quote(a));
                ProcessInfo pi;
                if (!CreateProcess(exe, b, IntPtr.Zero, IntPtr.Zero, nullStreams, flags, IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out pi)) throw Error();
                return new NativeChild(pi);
            } finally {
                if (read != IntPtr.Zero && read != new IntPtr(-1)) CloseHandle(read);
                if (capturedOutput == IntPtr.Zero && write != IntPtr.Zero && write != new IntPtr(-1)) CloseHandle(write);
            }
        }
    }
    internal sealed class NativeOutputCapture : IDisposable {
        public IntPtr WriteHandle { get; private set; }
        public Stream Input { get; private set; }
        public NativeOutputCapture() {
            var sa = new Native.SecurityAttributes { length = Marshal.SizeOf(typeof(Native.SecurityAttributes)), inherit = true };
            IntPtr read, write;
            if (!Native.CreatePipe(out read, out write, ref sa, 0)) throw Native.Error();
            WriteHandle = write;
            if (!Native.SetHandleInformation(read, 1, 0)) { Native.CloseHandle(read); Dispose(); throw Native.Error(); }
            Input = new FileStream(new SafeFileHandle(read, true), FileAccess.Read, 4096, false);
        }
        public void CloseWriter() { if (WriteHandle != IntPtr.Zero) Native.CloseHandle(WriteHandle); WriteHandle = IntPtr.Zero; }
        public void Dispose() { CloseWriter(); if (Input != null) Input.Dispose(); Input = null; }
    }
    internal sealed class NativeChild : IDisposable {
        internal Native.ProcessInfo Info;
        internal NativeChild(Native.ProcessInfo p) { Info = p; }
        public int Id { get { return (int)Info.pid; } }
        public bool Exited { get { return Native.WaitForSingleObject(Info.process, 0) == 0; } }
        public uint ExitCode { get { uint code; Native.GetExitCodeProcess(Info.process, out code); return code; } }
        public bool Wait(int ms) { return Native.WaitForSingleObject(Info.process, (uint)ms) == 0; }
        public void Resume() { if (Native.ResumeThread(Info.thread) == UInt32.MaxValue) throw Native.Error(); }
        public void Dispose() { if (Info.thread != IntPtr.Zero) Native.CloseHandle(Info.thread); if (Info.process != IntPtr.Zero) Native.CloseHandle(Info.process); Info.thread = Info.process = IntPtr.Zero; }
    }
    internal sealed class OwnedJob : IDisposable {
        private IntPtr handle;
        public NativeChild Host { get; private set; }
        public OwnedJob() {
            handle = Native.CreateJobObject(IntPtr.Zero, null); if (handle == IntPtr.Zero) throw Native.Error();
            var l = new Native.ExtendedLimit(); l.basic.flags = 0x2000; // KILL_ON_JOB_CLOSE; no breakaway.
            if (!Native.SetInformationJobObject(handle, 9, ref l, Marshal.SizeOf(l))) { Dispose(); throw Native.Error(); }
        }
        public void CreateSuspendedHost(string exe, string[] args) {
            Host = Native.Create(exe, args, Native.CREATE_SUSPENDED | Native.CREATE_NEW_CONSOLE, false);
            if (!Native.AssignProcessToJobObject(handle, Host.Info.process)) {
                Native.TerminateProcess(Host.Info.process, 201); Host.Wait(3000); throw Native.Error();
            }
        }
        public void CreateCaptured(string exe, string[] args, IntPtr output) {
            Host = Native.Create(exe, args, Native.CREATE_SUSPENDED | Native.CREATE_NEW_PROCESS_GROUP | 0x08000000, true, output);
            if (!Native.AssignProcessToJobObject(handle, Host.Info.process)) {
                Native.TerminateProcess(Host.Info.process, 201); Host.Wait(3000); throw Native.Error();
            }
        }
        public int ActiveCount {
            get { Native.Accounting a; if (handle == IntPtr.Zero || !Native.QueryInformationJobObject(handle, 1, out a, Marshal.SizeOf(typeof(Native.Accounting)), IntPtr.Zero)) throw Native.Error(); return (int)a.active; }
        }
        public bool WaitEmpty(int ms) { var w = Stopwatch.StartNew(); do { if (ActiveCount == 0) return true; Thread.Sleep(50); } while (w.ElapsedMilliseconds < ms); return ActiveCount == 0; }
        public void Kill() { if (!Native.TerminateJobObject(handle, 202)) throw Native.Error(); }
        public void Dispose() { if (handle != IntPtr.Zero) Native.CloseHandle(handle); handle = IntPtr.Zero; if (Host != null) Host.Dispose(); }
    }
}


