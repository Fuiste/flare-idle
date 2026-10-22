using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FlareIdle {
    // CPU-only scheduler tests. No process, network, NVML or CUDA calls.
    internal static class Tests {
        static int passed, failed;

        public static int Main() {
            try { Run().GetAwaiter().GetResult(); }
            catch(Exception error) { failed++; Console.WriteLine("fail: test runner: "+error); }
            Console.WriteLine("{0} passed; {1} failed",passed,failed);
            return failed==0 ? 0 : 1;
        }

        static async Task Run() {
            await Test("overlapping requests exclude mining until the last release",OverlappingRequests);
            await Test("duplicate and concurrent disposal is harmless",DuplicateDispose);
            await Test("pause stops mining and respects pending transitions",PauseStops);
            await Test("unavailable inference never calls the stop callback",UnavailableAdmission);
            await Test("failed stop leaks no active request or transition lock",FailedStop);
            await Test("a refused start never calls its start callback",RefusedStart);
            await Test("one hundred start versus admission races preserve exclusivity",StartAdmissionRaces);
        }

        static async Task Test(string name,Func<Task> test) {
            try { await Within(test()); passed++; Console.WriteLine("pass: "+name); }
            catch(Exception error) { failed++; Console.WriteLine("fail: "+name+": "+error); }
        }

        static void Check(bool value,string message) {
            if(!value) throw new InvalidOperationException(message);
        }

        static async Task Within(Task task) {
            if(await Task.WhenAny(task,Task.Delay(10000))!=task)
                throw new TimeoutException("scheduler operation did not complete within ten seconds");
            await task;
        }

        static async Task<T> Within<T>(Task<T> task) {
            await Within((Task)task); return await task;
        }

        static async Task OverlappingRequests() {
            int stops=0, starts=0;
            var gate=new WorkGate(async delegate { Interlocked.Increment(ref stops); await Task.Yield(); },delegate { return true; });
            gate.AllowInitialIdle(TimeSpan.FromMinutes(1));
            var oldIdle=gate.LastIdle;
            var tasks=new List<Task<IDisposable>>();
            for(int i=0;i<20;i++) tasks.Add(Task.Run(async delegate { return await gate.Enter(); }));
            var leases=await Within(Task.WhenAll(tasks));
            Check(gate.Active==20,"all twenty admitted requests must stay active");
            Check(stops==20,"every admission must complete its stop callback");
            Check(gate.LastIdle==oldIdle,"admission must not restart the idle timer");
            Check(!await gate.TryStart(delegate { return true; },delegate { starts++; }),"mining started during overlapping inference");
            for(int i=0;i<19;i++) leases[i].Dispose();
            Check(gate.Active==1,"one request should remain active");
            Check(gate.LastIdle==oldIdle,"only the last release may start the idle timer");
            Check(!await gate.TryStart(delegate { return true; },delegate { starts++; }),"mining started while the last request remained active");
            leases[19].Dispose();
            Check(gate.Active==0,"last release leaked an active request");
            Check(gate.LastIdle>oldIdle,"last release did not update idle time");
            Check(await gate.TryStart(delegate { return true; },delegate { starts++; }),"mining could not start after the last release");
            Check(starts==1,"blocked starts must not invoke their callback");
        }

        static async Task DuplicateDispose() {
            var gate=new WorkGate(delegate { return Task.FromResult(0); },delegate { return true; });
            var lease=await gate.Enter();
            var disposal=new List<Task>();
            for(int i=0;i<30;i++) disposal.Add(Task.Run(delegate { lease.Dispose(); }));
            await Within(Task.WhenAll(disposal));
            lease.Dispose();
            Check(gate.Active==0,"duplicate disposal changed the active count below zero");
            Check(await gate.TryStart(delegate { return true; },delegate { }),"duplicate disposal left mining blocked");
        }

        static async Task PauseStops() {
            int mining=0, stops=0;
            var enteredStop=new TaskCompletionSource<bool>();
            var allowStop=new TaskCompletionSource<bool>();
            var gate=new WorkGate(async delegate {
                Interlocked.Increment(ref stops);
                enteredStop.TrySetResult(true);
                await allowStop.Task;
                Interlocked.Exchange(ref mining,0);
            },delegate { return true; });
            Check(await gate.TryStart(delegate { return true; },delegate { Interlocked.Exchange(ref mining,1); }),"initial mining did not start");
            var pausing=gate.Pause();
            await Within(enteredStop.Task);
            bool mayStart=false;
            var pendingStart=gate.TryStart(delegate { return mayStart; },delegate { Interlocked.Exchange(ref mining,1); });
            Check(!pendingStart.IsCompleted,"a new start overtook the pause transition");
            allowStop.TrySetResult(true);
            await Within(pausing);
            Check(!await Within(pendingStart),"a disabled miner restarted after pause");
            Check(mining==0 && stops==1,"pause failed to stop the miner exactly once");
            Check(gate.Active==0,"pause fabricated an active inference request");
        }

        static async Task UnavailableAdmission() {
            int stops=0;
            var gate=new WorkGate(delegate { stops++; return Task.FromResult(0); },delegate { return false; });
            bool rejected=false;
            try { using(await gate.Enter()) { } }
            catch(InvalidOperationException) { rejected=true; }
            Check(rejected,"unavailable inference was admitted");
            Check(stops==0,"unavailable inference unnecessarily touched mining");
            Check(gate.Active==0,"unavailable inference leaked an active request");
            Check(await gate.TryStart(delegate { return true; },delegate { }),"unavailable admission leaked the transition lock");
        }

        static async Task FailedStop() {
            int calls=0;
            var gate=new WorkGate(async delegate {
                Interlocked.Increment(ref calls);
                await Task.Yield();
                throw new InvalidOperationException("injected stop failure");
            },delegate { return true; });
            for(int i=0;i<5;i++) {
                bool rejected=false;
                try { using(await gate.Enter()) { } }
                catch(InvalidOperationException error) { rejected=error.Message=="injected stop failure"; }
                Check(rejected,"failed mining stop returned an inference lease");
                Check(gate.Active==0,"failed mining stop leaked an active request");
                Check(await gate.TryStart(delegate { return true; },delegate { }),"failed mining stop leaked the transition lock");
            }
            Check(calls==5,"retry after a failed stop did not reach the stop callback");
        }

        static async Task RefusedStart() {
            int starts=0;
            var gate=new WorkGate(delegate { return Task.FromResult(0); },delegate { return true; });
            Check(!await gate.TryStart(delegate { return false; },delegate { starts++; }),"false eligibility was ignored");
            Check(starts==0,"false eligibility invoked the start callback");
            var lease=await gate.Enter();
            bool eligibilityCalled=false;
            Check(!await gate.TryStart(delegate { eligibilityCalled=true; return true; },delegate { starts++; }),"active inference was ignored");
            Check(!eligibilityCalled && starts==0,"active inference must reject before start eligibility or side effects");
            lease.Dispose();
        }

        static async Task StartAdmissionRaces() {
            for(int iteration=0;iteration<100;iteration++) {
                int mining=0;
                var begin=new TaskCompletionSource<bool>();
                var admitted=new TaskCompletionSource<bool>();
                var release=new TaskCompletionSource<bool>();
                var gate=new WorkGate(async delegate { await Task.Yield(); Interlocked.Exchange(ref mining,0); },delegate { return true; });
                var start=Task.Run(async delegate {
                    await begin.Task;
                    return await gate.TryStart(delegate { return true; },delegate { Interlocked.Exchange(ref mining,1); });
                });
                var enter=Task.Run(async delegate {
                    await begin.Task;
                    using(await gate.Enter()) {
                        Check(Volatile.Read(ref mining)==0,"miner was still running when admission returned");
                        admitted.TrySetResult(true);
                        await release.Task;
                    }
                });
                begin.TrySetResult(true);
                try {
                    var first=await Task.WhenAny(admitted.Task,enter);
                    if(first==enter) await enter;
                    await Within(admitted.Task);
                    await Within(start);
                    Check(gate.Active==1,"raced request did not hold its lease");
                    Check(Volatile.Read(ref mining)==0,"raced start left mining active after inference admission");
                } finally { release.TrySetResult(true); }
                await Within(enter);
                Check(gate.Active==0,"raced request leaked its lease");
            }
        }
    }
}

