using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FlareIdle;

// CPU-only process ownership tests. Child modes are finite and never load NVML/CUDA.
internal static class LifecycleTests
{
    private static readonly string Self = Assembly.GetExecutingAssembly().Location;
    private static int passed;

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--child")
        {
            File.WriteAllText(args[1], Process.GetCurrentProcess().Id.ToString());
            Console.WriteLine("child ready");
            Console.Out.Flush();
            Thread.Sleep(15000);
            return 0;
        }
        if (args.Length > 0 && args[0] == "--parent-child")
        {
            using (var child = Process.Start(new ProcessStartInfo(Self,
                "--child " + Native.Quote(args[1])) { UseShellExecute = false, CreateNoWindow = true }))
            {
                File.WriteAllText(args[2], child.Id.ToString());
                Thread.Sleep(15000);
            }
            return 0;
        }

        string root = Path.Combine(Path.GetDirectoryName(Self), "lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            AssignmentPrecedesResume(root);
            CloseKillsWorker(root);
            CloseKillsDescendants(root);
            CaptureCloseDoesNotKillOwner(root);
#if CORE_WORKER_TEST
            CoreWorkerStopsCleanly(root);
#endif
            Console.WriteLine("passed " + passed + " cpu-only lifecycle tests");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            foreach (string path in Directory.GetFiles(root))
                try { File.Delete(path); } catch { }
            try { Directory.Delete(root); } catch { }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void WaitFor(Func<bool> condition, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.ElapsedMilliseconds >= 5000) throw new Exception(message);
            Thread.Sleep(20);
        }
    }

    private static void AssignmentPrecedesResume(string root)
    {
        string marker = Path.Combine(root, "suspended.txt");
        using (var capture = new NativeOutputCapture())
        using (var job = new OwnedJob())
        {
            job.CreateCaptured(Self, new[] { "--child", marker }, capture.WriteHandle);
            capture.CloseWriter();
            Assert(job.ActiveCount == 1, "suspended worker was not assigned to the job");
            Thread.Sleep(200);
            Assert(!File.Exists(marker), "worker ran before explicit resume");
            Assert(!job.Host.Exited, "suspended worker unexpectedly exited");
            job.Host.Resume();
            WaitFor(() => File.Exists(marker), "resumed worker did not run");
            job.Kill();
            Assert(job.WaitEmpty(3000), "terminate job did not empty the job");
            Assert(job.Host.Wait(3000), "terminated worker remains alive");
        }
        passed++;
        Console.WriteLine("ok: assignment precedes resume; explicit stop empties job");
    }

    private static void CloseKillsWorker(string root)
    {
        string marker = Path.Combine(root, "close.txt");
        using (var capture = new NativeOutputCapture())
        {
            var job = new OwnedJob();
            try
            {
                job.CreateCaptured(Self, new[] { "--child", marker }, capture.WriteHandle);
                capture.CloseWriter();
                job.Host.Resume();
                WaitFor(() => File.Exists(marker), "close-test worker did not run");
                using (var process = Process.GetProcessById(job.Host.Id))
                {
                    job.Dispose();
                    Assert(process.WaitForExit(3000), "closing job did not kill worker");
                }
            }
            finally { job.Dispose(); }
        }
        passed++;
        Console.WriteLine("ok: closing job kills worker");
    }

    private static void CloseKillsDescendants(string root)
    {
        string marker = Path.Combine(root, "descendant.txt");
        string pidFile = Path.Combine(root, "descendant-pid.txt");
        using (var capture = new NativeOutputCapture())
        {
            var job = new OwnedJob();
            try
            {
                job.CreateCaptured(Self, new[] { "--parent-child", marker, pidFile }, capture.WriteHandle);
                capture.CloseWriter();
                job.Host.Resume();
                WaitFor(() => File.Exists(marker) && File.Exists(pidFile), "descendant did not start");
                WaitFor(() => job.ActiveCount >= 2, "descendant did not inherit the job");
                int childPid = Int32.Parse(File.ReadAllText(pidFile));
                using (var parent = Process.GetProcessById(job.Host.Id))
                using (var child = Process.GetProcessById(childPid))
                {
                    job.Dispose();
                    Assert(parent.WaitForExit(3000), "closing job did not kill parent worker");
                    Assert(child.WaitForExit(3000), "closing job did not kill descendant");
                }
            }
            finally { job.Dispose(); }
        }
        passed++;
        Console.WriteLine("ok: closing job kills descendants");
    }

    private static void CaptureCloseDoesNotKillOwner(string root)
    {
        for (int iteration = 0; iteration < 3; ++iteration)
        {
            string marker = Path.Combine(root, "reader-" + iteration + ".txt");
            var capture = new NativeOutputCapture();
            var job = new OwnedJob();
            try
            {
                job.CreateCaptured(Self, new[] { "--child", marker }, capture.WriteHandle);
                capture.CloseWriter();
                Task reader = Task.Run(delegate
                {
                    try
                    {
                        using (var stream = new StreamReader(capture.Input))
                            while (stream.ReadLine() != null) { }
                    }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                });
                job.Host.Resume();
                WaitFor(() => File.Exists(marker), "capture-test worker did not run");
                job.Kill();
                Assert(job.WaitEmpty(3000), "capture-test worker remained in job");
                job.Dispose();
                capture.Dispose();
                Assert(reader.Wait(3000), "capture reader did not finish after stop");
            }
            finally { job.Dispose(); capture.Dispose(); }
        }
        passed++;
        Console.WriteLine("ok: captured output teardown leaves owner alive");
    }

#if CORE_WORKER_TEST
    private static void CoreWorkerStopsCleanly(string root)
    {
        for (int iteration = 0; iteration < 3; ++iteration)
        {
            string marker = Path.Combine(root, "core-worker-" + iteration + ".txt");
            using (var worker = new Worker(Self, new[] { "--child", marker }, delegate { }))
            {
                WaitFor(() => File.Exists(marker), "core worker did not run");
                worker.Stop();
                WaitFor(() => worker.Exited, "core worker did not exit after stop");
            }
        }
        passed++;
        Console.WriteLine("ok: actual core worker stop and dispose leave owner alive");
    }
#endif
}

