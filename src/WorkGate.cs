using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace FlareIdle {
    // The same lock admits inference and starts mining. No sampling race can
    // launch a new miner after an inference request has acquired its lease.
    internal sealed class WorkGate {
        readonly SemaphoreSlim transition = new SemaphoreSlim(1,1);
        readonly Func<Task> stopMining;
        readonly Func<bool> ready;
        int requests;
        long idleTicks = DateTime.UtcNow.Ticks;
        public double LastHandoffMs { get; private set; }
        public int Active { get { return Volatile.Read(ref requests); } }
        public DateTime LastIdle { get { return new DateTime(Interlocked.Read(ref idleTicks),DateTimeKind.Utc); } }
        public WorkGate(Func<Task> stopMining, Func<bool> ready) { this.stopMining=stopMining; this.ready=ready; }
        public void AllowInitialIdle(TimeSpan delay) { Interlocked.Exchange(ref idleTicks,(DateTime.UtcNow-delay).Ticks); }
        public async Task<IDisposable> Enter() {
            var timer=Stopwatch.StartNew();
            await transition.WaitAsync().ConfigureAwait(false);
            try {
                if(!ready()) throw new InvalidOperationException("inference is unavailable");
                Interlocked.Increment(ref requests);
                try {
                    await stopMining().ConfigureAwait(false);
                    LastHandoffMs=timer.Elapsed.TotalMilliseconds;
                    return new Lease(this);
                } catch { Leave(); throw; }
            } finally { transition.Release(); }
        }
        public async Task<bool> TryStart(Func<bool> canStart, Action start) {
            await transition.WaitAsync().ConfigureAwait(false);
            try { if(Active!=0 || !canStart()) return false; start(); return true; }
            finally { transition.Release(); }
        }
        public async Task Pause() {
            await transition.WaitAsync().ConfigureAwait(false);
            try { await stopMining().ConfigureAwait(false); }
            finally { transition.Release(); }
        }
        void Leave() { if(Interlocked.Decrement(ref requests)==0) Interlocked.Exchange(ref idleTicks,DateTime.UtcNow.Ticks); }
        sealed class Lease : IDisposable {
            WorkGate owner;
            public Lease(WorkGate owner) { this.owner=owner; }
            public void Dispose() { var o=Interlocked.Exchange(ref owner,null); if(o!=null) o.Leave(); }
        }
    }
}

