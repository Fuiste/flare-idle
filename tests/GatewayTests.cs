using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FlareIdle {
    // CPU-only loopback fixtures. No real model, miner, key file, or GPU API.
    public static class GatewayTests {
        const string FixtureKey = "fixture-key-never-used-outside-loopback";

        sealed class LeaseCounter {
            public int Entered, Active, Released;
            public Task<IDisposable> Enter() { return Task.FromResult<IDisposable>(NewLease()); }
            public IDisposable NewLease() {
                Interlocked.Increment(ref Entered); Interlocked.Increment(ref Active);
                return new Lease(this);
            }
            sealed class Lease : IDisposable {
                LeaseCounter owner;
                public Lease(LeaseCounter owner) { this.owner = owner; }
                public void Dispose() {
                    var previous = Interlocked.Exchange(ref owner, null);
                    if (previous == null) return;
                    Interlocked.Decrement(ref previous.Active); Interlocked.Increment(ref previous.Released);
                }
            }
        }

        sealed class Backend : IDisposable {
            readonly HttpListener listener = new HttpListener();
            readonly ManualResetEventSlim release = new ManualResetEventSlim(false);
            readonly CancellationTokenSource stop = new CancellationTokenSource();
            public readonly ManualResetEventSlim Streaming = new ManualResetEventSlim(false);
            public int Requests, WrongAuthentication, LeakedHopHeader;
            public string MetricsQuery;
            public string Address;
            public Backend(int port) {
                Address = "http://127.0.0.1:" + port;
                listener.Prefixes.Add(Address + "/"); listener.Start();
                Task.Run(delegate { return Loop(); });
            }
            public void ReleaseStream() { release.Set(); }
            async Task Loop() {
                while (!stop.IsCancellationRequested) {
                    HttpListenerContext c;
                    try { c = await listener.GetContextAsync().ConfigureAwait(false); }
                    catch (HttpListenerException) { return; }
                    catch (ObjectDisposedException) { return; }
                    catch (InvalidOperationException) { return; }
                    Task.Run(delegate { return Serve(c); });
                }
            }
            async Task Serve(HttpListenerContext c) {
                Interlocked.Increment(ref Requests);
                if (c.Request.Headers["Authorization"] != "Bearer " + FixtureKey || c.Request.Headers["x-api-key"] != null)
                    Interlocked.Increment(ref WrongAuthentication);
                if (c.Request.Headers["X-Fixture-Hop"] != null) Interlocked.Increment(ref LeakedHopHeader);
                try {
                    using (var body = new StreamReader(c.Request.InputStream)) await body.ReadToEndAsync().ConfigureAwait(false);
                    string path = c.Request.Url.AbsolutePath;
                    if (path == "/metrics") {
                        MetricsQuery = c.Request.Url.Query;
                        c.Response.ContentType = "text/plain";
                        await Send(c.Response.OutputStream, "flare_active_requests 0\nflare_generated_tokens_total 123\n").ConfigureAwait(false);
                    } else if (path == "/v1/chat/completions") {
                        c.Response.ContentType = "text/event-stream"; c.Response.SendChunked = true;
                        await Send(c.Response.OutputStream, "data: {\"part\":1}\n\n").ConfigureAwait(false);
                        Streaming.Set();
                        await Task.Run(delegate { release.Wait(stop.Token); }).ConfigureAwait(false);
                        await Send(c.Response.OutputStream, "data: [DONE]\n\n").ConfigureAwait(false);
                    } else if (path == "/v1/error") {
                        c.Response.StatusCode = 422; c.Response.ContentType = "application/json";
                        await Send(c.Response.OutputStream, "{\"error\":\"fixture rejection\"}").ConfigureAwait(false);
                    } else if (path == "/v1/redirect") {
                        c.Response.StatusCode = 302; c.Response.RedirectLocation = "http://127.0.0.1:9/not-followed";
                        c.Response.ContentLength64 = 0;
                    } else {
                        c.Response.ContentType = "application/json";
                        c.Response.Headers["X-Fixture-Response"] = "preserved";
                        await Send(c.Response.OutputStream, "{\"data\":[{\"id\":\"fixture-model\"}]}").ConfigureAwait(false);
                    }
                    c.Response.Close();
                } catch { try { c.Response.Abort(); } catch { } }
            }
            static async Task Send(Stream output, string text) {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                await output.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
            public void Dispose() {
                release.Set(); stop.Cancel();
                try { listener.Abort(); listener.Close(); } catch { }
            }
        }

        static int FreePort() {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
        }
        static int DifferentPort(int other) { int port; do { port = FreePort(); } while (port == other); return port; }
        static bool Eventually(Func<bool> condition, int milliseconds) {
            var timer = Stopwatch.StartNew();
            do { if (condition()) return true; Thread.Sleep(10); } while (timer.ElapsedMilliseconds < milliseconds);
            return condition();
        }
        static HttpClient Client() {
            return new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        }
        static HttpRequestMessage Request(string method, string url, string key) {
            var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (key != null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            if (method == "POST") request.Content = new StringContent("{\"fixture\":true}", Encoding.UTF8, "application/json");
            return request;
        }

        public static void Run(Action<bool, string> assert) {
            if (assert == null) throw new ArgumentNullException("assert");
            RunAsync(assert).GetAwaiter().GetResult();
        }

        static async Task RunAsync(Action<bool, string> assert) {
            int backendPort = FreePort(), gatewayPort = DifferentPort(backendPort);
            var leases = new LeaseCounter(); bool ready = true;
            using (var backend = new Backend(backendPort))
            using (var gateway = new Gateway("127.0.0.1", gatewayPort, backend.Address, FixtureKey, leases.Enter, delegate { return ready; }))
            using (var http = Client()) {
                gateway.Start(); string origin = "http://127.0.0.1:" + gatewayPort;

                using (var response = await http.GetAsync(origin + "/health").ConfigureAwait(false))
                    assert(response.StatusCode == HttpStatusCode.OK && backend.Requests == 0, "health is synthetic and unauthenticated");
                ready = false;
                using (var response = await http.GetAsync(origin + "/health").ConfigureAwait(false))
                    assert(response.StatusCode == HttpStatusCode.ServiceUnavailable, "health reports unavailable while stopped");
                ready = true;

                using (var request = Request("POST", origin + "/v1/chat/completions", null))
                using (var response = await http.SendAsync(request).ConfigureAwait(false))
                    assert(response.StatusCode == HttpStatusCode.Unauthorized && leases.Entered == 0 && backend.Requests == 0,
                        "missing authentication never admits inference or reaches backend (status=" + (int)response.StatusCode + ", leases=" + leases.Entered + ", backend=" + backend.Requests + ", body=" + await response.Content.ReadAsStringAsync().ConfigureAwait(false) + ")");
                using (var request = Request("POST", origin + "/v1/chat/completions", "incorrect"))
                using (var response = await http.SendAsync(request).ConfigureAwait(false))
                    assert(response.StatusCode == HttpStatusCode.Unauthorized && leases.Entered == 0,
                        "wrong authentication never admits inference");

                foreach (string path in new[] { "/sleep", "/wake_up", "/v1/../sleep" })
                    using (var request = Request("GET", origin + path, FixtureKey))
                    using (var response = await http.SendAsync(request).ConfigureAwait(false))
                        assert(response.StatusCode == HttpStatusCode.NotFound && backend.Requests == 0,
                            "private control route is unavailable: " + path);

                using (var response = await http.GetAsync(origin + "/metrics?format=prometheus").ConfigureAwait(false)) {
                    string content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    assert(response.StatusCode == HttpStatusCode.OK && content.IndexOf("flare_active_requests 0", StringComparison.Ordinal) >= 0,
                        "public aggregate metrics are forwarded without authentication");
                    assert(backend.MetricsQuery == "?format=prometheus" && leases.Entered == 0 && gateway.RequestCount == 0,
                        "metrics preserves the original format query without admission or generation count");
                }
                int metricsRequests = backend.Requests;
                using (var request = Request("POST", origin + "/metrics", FixtureKey))
                using (var response = await http.SendAsync(request).ConfigureAwait(false))
                    assert(response.StatusCode == HttpStatusCode.NotFound && backend.Requests == metricsRequests && leases.Entered == 0,
                        "metrics remains read-only and does not admit post requests");
                ready = false;
                using (var response = await http.GetAsync(origin + "/metrics?format=json").ConfigureAwait(false))
                    assert(response.StatusCode == HttpStatusCode.ServiceUnavailable && backend.Requests == metricsRequests,
                        "metrics returns unavailable without contacting an unready backend");
                ready = true;

                using (var request = Request("GET", origin + "/v1/models?fixture=1", FixtureKey)) {
                    request.Headers.TryAddWithoutValidation("Connection", "X-Fixture-Hop");
                    request.Headers.TryAddWithoutValidation("X-Fixture-Hop", "must-not-forward");
                    using (var response = await http.SendAsync(request).ConfigureAwait(false)) {
                        string content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        assert(response.StatusCode == HttpStatusCode.OK && content.IndexOf("fixture-model", StringComparison.Ordinal) >= 0,
                            "models response and query are forwarded");
                        assert(response.Headers.Contains("X-Fixture-Response"), "end-to-end response headers are preserved");
                        assert(leases.Entered == 0 && backend.LeakedHopHeader == 0, "models bypasses admission and hop-by-hop request headers are stripped");
                    }
                }

                int before = backend.Requests;
                string status = OversizedHeaders(gatewayPort);
                assert(status.IndexOf(" 413 ", StringComparison.Ordinal) >= 0 && leases.Entered == 0 && backend.Requests == before,
                    "declared body over 32 mib is rejected before upload or admission");

                using (var request = Request("POST", origin + "/v1/chat/completions", FixtureKey))
                using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false)) {
                    assert(response.StatusCode == HttpStatusCode.OK && backend.Streaming.Wait(3000), "stream reaches backend");
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var reader = new StreamReader(stream)) {
                        string first = await reader.ReadLineAsync().ConfigureAwait(false);
                        assert(first == "data: {\"part\":1}" && leases.Active == 1 && leases.Released == 0,
                            "stream is forwarded immediately and retains its inference lease");
                        backend.ReleaseStream();
                        string rest = await reader.ReadToEndAsync().ConfigureAwait(false);
                        assert(rest.IndexOf("[DONE]", StringComparison.Ordinal) >= 0, "stream is forwarded through completion");
                    }
                }
                assert(Eventually(delegate { return leases.Active == 0 && leases.Released == 1; }, 3000), "completed stream releases its lease exactly once");
                assert(gateway.RequestCount == 1, "only generation requests contribute to request count");

                using (var request = Request("POST", origin + "/v1/messages", null)) {
                    request.Headers.TryAddWithoutValidation("x-api-key", FixtureKey);
                    using (var response = await http.SendAsync(request).ConfigureAwait(false))
                        assert(response.StatusCode == HttpStatusCode.OK, "messages supports x-api-key authentication");
                }
                assert(Eventually(delegate { return leases.Active == 0; }, 3000) && backend.WrongAuthentication == 0,
                    "backend receives only canonical validated bearer authentication");
                int admitted = leases.Entered;
                using (var request = Request("POST", origin + "/v1/messages", "incorrect")) {
                    request.Headers.TryAddWithoutValidation("x-api-key", FixtureKey);
                    using (var response = await http.SendAsync(request).ConfigureAwait(false))
                        assert(response.StatusCode == HttpStatusCode.Unauthorized && leases.Entered == admitted,
                            "conflicting authentication forms are rejected");
                }

                using (var request = Request("GET", origin + "/v1/error", FixtureKey))
                using (var response = await http.SendAsync(request).ConfigureAwait(false))
                    assert((int)response.StatusCode == 422 && (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).IndexOf("fixture rejection", StringComparison.Ordinal) >= 0,
                        "backend error status and body are retained");
                using (var request = Request("GET", origin + "/v1/redirect", FixtureKey))
                using (var response = await http.SendAsync(request).ConfigureAwait(false))
                    assert(response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location.Port == 9,
                        "backend redirect is returned rather than followed");

                ready = false;
                before = backend.Requests; admitted = leases.Entered;
                using (var request = Request("POST", origin + "/v1/messages", FixtureKey))
                using (var response = await http.SendAsync(request).ConfigureAwait(false))
                    assert(response.StatusCode == HttpStatusCode.ServiceUnavailable && backend.Requests == before && leases.Entered == admitted,
                        "not-ready generation is rejected before admission");
                gateway.Stop();
            }
            await LateAdmission(assert).ConfigureAwait(false);
            await StoppedStream(assert).ConfigureAwait(false);
            await DisconnectedStream(assert).ConfigureAwait(false);
        }

        static string OversizedHeaders(int port) {
            using (var client = new TcpClient()) {
                client.ReceiveTimeout = 5000; client.SendTimeout = 5000; client.Connect(IPAddress.Loopback, port);
                using (var stream = client.GetStream()) {
                    string request = "POST /v1/messages HTTP/1.1\r\nHost: 127.0.0.1:" + port +
                        "\r\nAuthorization: Bearer " + FixtureKey + "\r\nContent-Type: application/json\r\nContent-Length: 33554433\r\nConnection: close\r\n\r\n";
                    byte[] bytes = Encoding.ASCII.GetBytes(request); stream.Write(bytes, 0, bytes.Length); stream.Flush();
                    return new StreamReader(stream, Encoding.ASCII).ReadLine() ?? "";
                }
            }
        }

        static async Task LateAdmission(Action<bool, string> assert) {
            int backendPort = FreePort(), gatewayPort = DifferentPort(backendPort);
            var leases = new LeaseCounter(); var entering = new ManualResetEventSlim(false);
            var pending = new TaskCompletionSource<IDisposable>();
            using (var backend = new Backend(backendPort))
            using (var gateway = new Gateway("127.0.0.1", gatewayPort, backend.Address, FixtureKey,
                delegate { entering.Set(); return pending.Task; }, delegate { return true; }))
            using (var http = Client()) {
                gateway.Start();
                using (var request = Request("POST", "http://127.0.0.1:" + gatewayPort + "/v1/messages", FixtureKey)) {
                    Task<HttpResponseMessage> sent = http.SendAsync(request);
                    assert(entering.Wait(3000), "pending admission is reached");
                    gateway.Stop();
                    pending.SetResult(leases.NewLease());
                    try { using (var response = await sent.ConfigureAwait(false)) { } } catch (HttpRequestException) { } catch (TaskCanceledException) { }
                    assert(Eventually(delegate { return leases.Active == 0 && leases.Released == 1; }, 3000),
                        "shutdown returns a lease granted after cancellation");
                    assert(backend.Requests == 0, "cancelled admission never dispatches to backend");
                }
            }
            entering.Dispose();
        }

        static async Task StoppedStream(Action<bool, string> assert) {
            int backendPort = FreePort(), gatewayPort = DifferentPort(backendPort);
            var leases = new LeaseCounter();
            using (var backend = new Backend(backendPort))
            using (var gateway = new Gateway("127.0.0.1", gatewayPort, backend.Address, FixtureKey, leases.Enter, delegate { return true; }))
            using (var http = Client()) {
                gateway.Start();
                using (var request = Request("POST", "http://127.0.0.1:" + gatewayPort + "/v1/chat/completions", FixtureKey))
                using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false)) {
                    assert(backend.Streaming.Wait(3000) && leases.Active == 1, "stopped-stream fixture holds an active lease");
                    gateway.Stop();
                    assert(Eventually(delegate { return leases.Active == 0 && leases.Released == 1; }, 3000),
                        "gateway stop cancels an idle upstream stream and releases its lease");
                }
            }
        }

        static async Task DisconnectedStream(Action<bool, string> assert) {
            int backendPort = FreePort(), gatewayPort = DifferentPort(backendPort);
            var leases = new LeaseCounter();
            using (var backend = new Backend(backendPort))
            using (var gateway = new Gateway("127.0.0.1", gatewayPort, backend.Address, FixtureKey, leases.Enter, delegate { return true; }))
            using (var http = Client()) {
                gateway.Start();
                using (var request = Request("POST", "http://127.0.0.1:" + gatewayPort + "/v1/chat/completions", FixtureKey)) {
                    var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    using (response) {
                        var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                        using (var reader = new StreamReader(stream)) {
                            await reader.ReadLineAsync().ConfigureAwait(false);
                            assert(leases.Active == 1, "disconnect fixture holds an active lease");
                        }
                    }
                    assert(Eventually(delegate { return leases.Active == 0 && leases.Released == 1; }, 10000),
                        "disconnected sse client is detected and its upstream lease is released");
                }
            }
        }

#if GATEWAY_TEST_EXE
        public static int Main() {
            int checks = 0;
            try {
                Run(delegate(bool okay, string name) { checks++; if (!okay) throw new InvalidOperationException(name); Console.WriteLine("pass: " + name); });
                Console.WriteLine("gateway checks passed: " + checks); return 0;
            } catch (Exception error) { Console.Error.WriteLine("gateway test failed: " + error.GetBaseException().Message); Console.Error.WriteLine(error.GetBaseException().StackTrace); return 1; }
        }
#endif
    }
}

