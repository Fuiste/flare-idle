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
    // Only this endpoint admits requests to the resident model. A lease stops
    // mining before dispatch and keeps it stopped through the complete stream.
    public sealed class Gateway : IDisposable {
        const int MaxBodyBytes = 32 * 1024 * 1024;
        readonly string bindAddress;
        readonly int port;
        readonly string backendAuthority;
        readonly string apiKey;
        readonly Func<Task<IDisposable>> enterInference;
        readonly Func<bool> isReady;
        readonly HttpClient client;
        readonly SemaphoreSlim slots = new SemaphoreSlim(16, 16);
        readonly object lifecycle = new object();
        readonly HashSet<Task> handlers = new HashSet<Task>();
        HttpListener listener;
        CancellationTokenSource stopping;
        bool disposed;
        long requestCount;

        static readonly HashSet<string> HopHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
            "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host", "Content-Length", "Expect"
        };

        public Gateway(string bindAddress, int port, string backendBase, string apiKey,
            Func<Task<IDisposable>> enterInference, Func<bool> isReady) {
            IPAddress address;
            if (!IPAddress.TryParse(bindAddress, out address) || address.Equals(IPAddress.Any) ||
                address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast))
                throw new ArgumentException("an exact local IP address is required", "bindAddress");
            if (port < 1024 || port > 65535) throw new ArgumentOutOfRangeException("port");
            Uri backend;
            IPAddress backendAddress;
            if (!Uri.TryCreate(backendBase, UriKind.Absolute, out backend) || backend.Scheme != "http" ||
                !IPAddress.TryParse(backend.Host.Trim('[', ']'), out backendAddress) || !IPAddress.IsLoopback(backendAddress) ||
                backend.Port < 1024 || !String.IsNullOrEmpty(backend.UserInfo) ||
                (backend.AbsolutePath != "/" && backend.AbsolutePath != "") ||
                !String.IsNullOrEmpty(backend.Query) || !String.IsNullOrEmpty(backend.Fragment))
                throw new ArgumentException("the backend must be a fixed loopback HTTP origin", "backendBase");
            if (String.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("an API key is required", "apiKey");
            if (enterInference == null) throw new ArgumentNullException("enterInference");
            if (isReady == null) throw new ArgumentNullException("isReady");
            this.bindAddress = address.ToString(); this.port = port;
            backendAuthority = backend.GetLeftPart(UriPartial.Authority);
            this.apiKey = apiKey; this.enterInference = enterInference; this.isReady = isReady;
            // Framework HTTP defaults can otherwise serialize four model slots
            // behind a two-connection service point. This affects this origin only.
            var servicePoint = ServicePointManager.FindServicePoint(backend, null);
            servicePoint.ConnectionLimit = 16;
            servicePoint.Expect100Continue = false;
            servicePoint.UseNagleAlgorithm = false;
            var transport = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None, UseCookies = false };
            client = new HttpClient(transport) { Timeout = Timeout.InfiniteTimeSpan };
        }

        public long RequestCount { get { return Interlocked.Read(ref requestCount); } }

        static string Prefix(string address, int port) {
            if (address.IndexOf(':') >= 0) address = "[" + address + "]";
            return "http://" + address + ":" + port + "/";
        }

        public void Start() {
            lock (lifecycle) {
                if (disposed) throw new ObjectDisposedException("Gateway");
                if (listener != null) return;
                var next = new HttpListener { IgnoreWriteExceptions = false };
                next.Prefixes.Add(Prefix(bindAddress, port));
                if (bindAddress != "127.0.0.1") next.Prefixes.Add(Prefix("127.0.0.1", port));
                var cancel = new CancellationTokenSource();
                try { next.Start(); }
                catch { next.Close(); cancel.Dispose(); throw; }
                listener = next; stopping = cancel;
                // GetContextAsync is interrupted by Close; no dedicated foreground thread.
                Task.Run(delegate { return AcceptLoop(next, cancel.Token); });
            }
        }

        public void Stop() {
            HttpListener old;
            CancellationTokenSource cancel;
            lock (lifecycle) { old = listener; cancel = stopping; listener = null; stopping = null; }
            if (cancel != null) { try { cancel.Cancel(); } catch (ObjectDisposedException) { } }
            if (old != null) { try { old.Abort(); old.Close(); } catch { } }
            // Cancellation registration aborts active request streams. Their finally
            // blocks own the admission leases; Stop never releases a live lease early.
        }

        async Task AcceptLoop(HttpListener server, CancellationToken cancellation) {
            while (!cancellation.IsCancellationRequested) {
                HttpListenerContext context;
                try { context = await server.GetContextAsync().ConfigureAwait(false); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; }
                if (!slots.Wait(0)) {
                    try { await JsonError(context.Response, 503, "gateway is busy").ConfigureAwait(false); }
                    catch { Abort(context.Response); }
                    continue;
                }
                var task = Handle(context, cancellation);
                lock (handlers) handlers.Add(task);
                task.ContinueWith(delegate(Task finished) {
                    lock (handlers) handlers.Remove(finished);
                    // Observe a catastrophic handler failure without logging request data.
                    if (finished.IsFaulted) { var ignored = finished.Exception; }
                    slots.Release();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        bool Ready() { try { return isReady(); } catch { return false; } }

        static bool SameSecret(string supplied, string expected) {
            supplied = supplied ?? "";
            int different = supplied.Length ^ expected.Length;
            // Compare every expected character regardless of the matching prefix.
            for (int i = 0; i < expected.Length; i++)
                different |= expected[i] ^ (i < supplied.Length ? supplied[i] : 0);
            return different == 0;
        }

        bool Authorized(HttpListenerRequest request, string path) {
            string authorization = request.Headers["Authorization"];
            string alternate = request.Headers["x-api-key"];
            bool hasBearer = !String.IsNullOrEmpty(authorization);
            bool hasAlternate = !String.IsNullOrEmpty(alternate);
            string bearer = hasBearer && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authorization.Substring(7) : "";
            bool bearerOk = SameSecret(bearer, apiKey);
            bool alternateOk = SameSecret(alternate, apiKey);
            bool messages = path == "/v1/messages" || path.StartsWith("/v1/messages/", StringComparison.Ordinal);
            // If both forms are supplied, both must agree, matching Flare's contract.
            if (hasBearer && !bearerOk) return false;
            if (hasAlternate && (!messages || !alternateOk)) return false;
            return (hasBearer && bearerOk) || (messages && hasAlternate && alternateOk);
        }

        static bool GenerationPath(string path) {
            return path == "/v1/chat/completions" || path == "/v1/completions" ||
                path == "/v1/messages" || path == "/v1/responses";
        }

        async Task Handle(HttpListenerContext context, CancellationToken serverCancellation) {
            IDisposable admission = null;
            bool forwarded = false;
            var response = context.Response;
            using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation)) {
                cancellation.CancelAfter(TimeSpan.FromMinutes(30));
                using (cancellation.Token.Register(delegate {
                    try { context.Request.InputStream.Close(); } catch { }
                    Abort(response);
                })) {
                    try {
                        string path = context.Request.Url.AbsolutePath;
                        string method = context.Request.HttpMethod;
                        bool metrics = path == "/metrics" && method == "GET";
                        if (path == "/health" && method == "GET") {
                            bool ready = Ready();
                            await WriteJson(response, ready ? 200 : 503, ready ? "{\"status\":\"ok\"}" : "{\"status\":\"unavailable\"}").ConfigureAwait(false);
                            return;
                        }
                        if (!metrics && !path.StartsWith("/v1/", StringComparison.Ordinal)) {
                            await JsonError(response, 404, "route not found").ConfigureAwait(false); return;
                        }
                        if (method != "GET" && method != "HEAD" && method != "POST") {
                            try { response.AddHeader("Allow", "GET, HEAD, POST"); } catch (ArgumentException) { }
                            await JsonError(response, 405, "method not allowed").ConfigureAwait(false); return;
                        }
                        // Preserve Flare's existing public aggregate metrics endpoint;
                        // it neither executes a model request nor acquires a GPU lease.
                        if (!metrics && !Authorized(context.Request, path)) {
                            // Some Framework HTTP.sys builds reserve challenge headers.
                            // A rejected challenge header must never turn a 401 into 503.
                            try { response.AddHeader("WWW-Authenticate", "Bearer"); } catch (ArgumentException) { }
                            await JsonError(response, 401, "invalid API key").ConfigureAwait(false); return;
                        }
                        if (!Ready()) { await JsonError(response, 503, "model is not ready").ConfigureAwait(false); return; }
                        if (context.Request.ContentLength64 > MaxBodyBytes) {
                            await JsonError(response, 413, "request body is too large").ConfigureAwait(false); return;
                        }
                        byte[] body;
                        try { body = await ReadBody(context.Request, cancellation.Token).ConfigureAwait(false); }
                        catch (BodyLimitException) {
                            SafeError(response, 413, "request body is too large").GetAwaiter().GetResult(); return;
                        }
                        catch (BodyTimeoutException) {
                            SafeError(response, 408, "request body timed out").GetAwaiter().GetResult(); return;
                        }
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (method == "POST") {
                            admission = await Acquire(cancellation.Token).ConfigureAwait(false);
                            cancellation.Token.ThrowIfCancellationRequested();
                            if (!Ready()) { await JsonError(response, 503, "model is not ready").ConfigureAwait(false); return; }
                            if (GenerationPath(path)) Interlocked.Increment(ref requestCount);
                        }
                        // Concatenating an already validated path with a fixed authority
                        // prevents absolute-form request targets changing the backend.
                        string raw = context.Request.RawUrl;
                        if (String.IsNullOrEmpty(raw) || raw[0] != '/' || raw.StartsWith("//", StringComparison.Ordinal)) {
                            await JsonError(response, 400, "invalid request target").ConfigureAwait(false); return;
                        }
                        var destination = new Uri(backendAuthority + raw, UriKind.Absolute);
                        if (!destination.AbsolutePath.StartsWith("/v1/", StringComparison.Ordinal) &&
                            !(metrics && destination.AbsolutePath == "/metrics")) {
                            await JsonError(response, 404, "route not found").ConfigureAwait(false); return;
                        }
                        using (var upstreamRequest = new HttpRequestMessage(new HttpMethod(method), destination)) {
                            if (body.Length != 0 || context.Request.HasEntityBody || method == "POST")
                                upstreamRequest.Content = new ByteArrayContent(body);
                            var excluded = ConnectionHeaders(context.Request.Headers["Connection"]);
                            foreach (string header in context.Request.Headers.AllKeys) {
                                if (excluded.Contains(header) || header.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                                    header.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)) continue;
                                string[] values = context.Request.Headers.GetValues(header);
                                if (!upstreamRequest.Headers.TryAddWithoutValidation(header, values) && upstreamRequest.Content != null)
                                    upstreamRequest.Content.Headers.TryAddWithoutValidation(header, values);
                            }
                            upstreamRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                            upstreamRequest.Headers.ExpectContinue = false;
                            using (var upstream = await client.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead,
                                cancellation.Token).ConfigureAwait(false))
                            using (cancellation.Token.Register(delegate { try { upstream.Dispose(); } catch { } })) {
                                response.StatusCode = (int)upstream.StatusCode;
                                string connection = null;
                                IEnumerable<string> connectionValues;
                                if (upstream.Headers.TryGetValues("Connection", out connectionValues)) connection = String.Join(",", connectionValues);
                                var responseExcluded = ConnectionHeaders(connection);
                                CopyResponseHeaders(response, upstream.Headers, responseExcluded);
                                CopyResponseHeaders(response, upstream.Content.Headers, responseExcluded);
                                if (upstream.Content.Headers.ContentType != null) response.ContentType = upstream.Content.Headers.ContentType.ToString();
                                if (upstream.Content.Headers.ContentLength.HasValue) response.ContentLength64 = upstream.Content.Headers.ContentLength.Value;
                                else if (method != "HEAD" && response.StatusCode != 204 && response.StatusCode != 304) response.SendChunked = true;
                                forwarded = true;
                                if (method != "HEAD" && response.StatusCode != 204 && response.StatusCode != 304) {
                                    using (var input = await upstream.Content.ReadAsStreamAsync().ConfigureAwait(false)) {
                                        bool eventStream = upstream.Content.Headers.ContentType != null &&
                                            String.Equals(upstream.Content.Headers.ContentType.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
                                        // A declared content length must stay byte-for-byte exact.
                                        eventStream = eventStream && !upstream.Content.Headers.ContentLength.HasValue;
                                        await CopyStream(input, response.OutputStream, eventStream, cancellation.Token).ConfigureAwait(false);
                                    }
                                }
                                response.Close();
                            }
                        }
                    } catch (OperationCanceledException) {
                        if (!forwarded && !serverCancellation.IsCancellationRequested) SafeError(response, 504, "request timed out").GetAwaiter().GetResult();
                        else Abort(response);
                    } catch (HttpRequestException) {
                        if (!forwarded) SafeError(response, 502, "model connection failed").GetAwaiter().GetResult(); else Abort(response);
                    } catch (IOException) { Abort(response); }
                    catch (HttpListenerException) { Abort(response); }
                    catch (ObjectDisposedException) { Abort(response); }
                    catch {
                        if (!forwarded) SafeError(response, 503, "request could not be admitted").GetAwaiter().GetResult(); else Abort(response);
                    } finally {
                        cancellation.Cancel();
                        if (admission != null) { try { admission.Dispose(); } catch { } }
                    }
                }
            }
        }

        async Task<IDisposable> Acquire(CancellationToken cancellation) {
            var acquiring = enterInference();
            if (acquiring == null) throw new InvalidOperationException("missing admission task");
            var interrupted = Task.Delay(Timeout.Infinite, cancellation);
            if (await Task.WhenAny(acquiring, interrupted).ConfigureAwait(false) != acquiring) {
                // If admission finishes after a disconnect/shutdown, return that lease.
                acquiring.ContinueWith(delegate(Task<IDisposable> late) {
                    if (late.Status == TaskStatus.RanToCompletion && late.Result != null) { try { late.Result.Dispose(); } catch { } }
                    else if (late.IsFaulted) { var ignored = late.Exception; }
                }, TaskScheduler.Default);
                cancellation.ThrowIfCancellationRequested();
            }
            var lease = await acquiring.ConfigureAwait(false);
            if (lease == null) throw new InvalidOperationException("missing admission lease");
            return lease;
        }

        sealed class BodyLimitException : Exception { }
        sealed class BodyTimeoutException : Exception { }

        static async Task<byte[]> ReadBody(HttpListenerRequest request, CancellationToken cancellation) {
            if (!request.HasEntityBody) return new byte[0];
            using (var bytes = new MemoryStream()) {
                byte[] buffer = new byte[16384];
                var elapsed = Stopwatch.StartNew();
                while (true) {
                    TimeSpan remaining = TimeSpan.FromSeconds(30) - elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero) { request.InputStream.Close(); throw new BodyTimeoutException(); }
                    var reading = request.InputStream.ReadAsync(buffer, 0, buffer.Length, cancellation);
                    if (await Task.WhenAny(reading, Task.Delay(remaining, cancellation)).ConfigureAwait(false) != reading) {
                        request.InputStream.Close();
                        cancellation.ThrowIfCancellationRequested();
                        throw new BodyTimeoutException();
                    }
                    int count = await reading.ConfigureAwait(false);
                    if (count == 0) return bytes.ToArray();
                    if (bytes.Length + count > MaxBodyBytes) { request.InputStream.Close(); throw new BodyLimitException(); }
                    bytes.Write(buffer, 0, count);
                }
            }
        }

        static HashSet<string> ConnectionHeaders(string value) {
            var excluded = new HashSet<string>(HopHeaders, StringComparer.OrdinalIgnoreCase);
            if (!String.IsNullOrEmpty(value)) foreach (var name in value.Split(',')) if (name.Trim().Length != 0) excluded.Add(name.Trim());
            return excluded;
        }

        static void CopyResponseHeaders(HttpListenerResponse response, IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers, HashSet<string> excluded) {
            foreach (var entry in headers) {
                if (excluded.Contains(entry.Key) || entry.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.Key.Equals("Location", StringComparison.OrdinalIgnoreCase)) {
                    foreach (var value in entry.Value) response.RedirectLocation = value;
                    continue;
                }
                foreach (var value in entry.Value) {
                    try {
                        HttpResponseHeader known;
                        if (Enum.TryParse<HttpResponseHeader>(entry.Key.Replace("-", ""), true, out known)) response.Headers.Add(known, value);
                        else response.AppendHeader(entry.Key, value);
                    } catch (ArgumentException) { }
                }
            }
        }

        static async Task CopyStream(Stream input, Stream output, bool eventStream, CancellationToken cancellation) {
            var buffer = new byte[16384];
            byte[] heartbeat = Encoding.UTF8.GetBytes(": keepalive\n\n");
            Task<int> reading = null;
            while (true) {
                cancellation.ThrowIfCancellationRequested();
                if (reading == null) reading = input.ReadAsync(buffer, 0, buffer.Length, cancellation);
                if (eventStream && await Task.WhenAny(reading, Task.Delay(5000, cancellation)).ConfigureAwait(false) != reading) {
                    cancellation.ThrowIfCancellationRequested();
                    // Valid SSE comments also surface a client that disconnected while
                    // the model was busy and had not produced another token yet.
                    await output.WriteAsync(heartbeat, 0, heartbeat.Length, cancellation).ConfigureAwait(false);
                    await output.FlushAsync(cancellation).ConfigureAwait(false);
                    continue;
                }
                int count = await reading.ConfigureAwait(false); reading = null;
                if (count == 0) return;
                await output.WriteAsync(buffer, 0, count, cancellation).ConfigureAwait(false);
                await output.FlushAsync(cancellation).ConfigureAwait(false);
            }
        }

        static Task JsonError(HttpListenerResponse response, int status, string message) {
            // All callers supply constant strings; no client/model text enters errors.
            return WriteJson(response, status, "{\"error\":{\"message\":\"" + message + "\",\"type\":\"gateway_error\"}}");
        }
        static async Task WriteJson(HttpListenerResponse response, int status, string json) {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            response.StatusCode = status; response.ContentType = "application/json";
            response.KeepAlive = false; response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            response.Close();
        }
        static async Task SafeError(HttpListenerResponse response, int status, string message) {
            try { await JsonError(response, status, message).ConfigureAwait(false); } catch { Abort(response); }
        }
        static void Abort(HttpListenerResponse response) { try { response.Abort(); } catch { } }

        public void Dispose() {
            lock (lifecycle) { if (disposed) return; disposed = true; }
            Stop();
            client.Dispose();
            // Active handlers release slots after cancellation; do not dispose their semaphore.
        }
    }
}

