using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenRhinoMcp.Core;

namespace OpenRhinoMcp.Bridge
{
    /// <summary>
    /// Localhost-only TCP server speaking newline-delimited JSON (see docs/PROTOCOL.md).
    /// Socket I/O runs on background threads; every request is executed on Rhino's UI thread.
    /// </summary>
    public sealed class BridgeServer
    {
        public const int ProtocolVersion = 1;
        const int DefaultTimeoutMs = 120000;
        const int MaxTimeoutMs = 600000;

        TcpListener _listener;
        Thread _acceptThread;
        readonly List<TcpClient> _clients = new List<TcpClient>();
        volatile bool _running;
        long _requestCount;

        public int Port { get; private set; }
        public string Token { get; private set; }
        public bool IsRunning => _running;
        public long RequestCount => Interlocked.Read(ref _requestCount);
        public int ClientCount { get { lock (_clients) return _clients.Count; } }

        public void Start(int preferredPort, string pluginVersion)
        {
            Token = NewToken();
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, preferredPort);
                _listener.Start();
            }
            catch (SocketException)
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
            }
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "OpenRhinoMcp.Accept" };
            _acceptThread.Start();
            Discovery.Write(Port, Token, pluginVersion, ProtocolVersion);
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            lock (_clients)
            {
                foreach (var c in _clients) { try { c.Close(); } catch { } }
                _clients.Clear();
            }
            Discovery.Delete(Token);
        }

        static string NewToken()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder(64);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    client.NoDelay = true;
                    lock (_clients) _clients.Add(client);
                    var t = new Thread(() => ClientLoop(client)) { IsBackground = true, Name = "OpenRhinoMcp.Client" };
                    t.Start();
                }
                catch (SocketException) { if (!_running) break; }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; }
            }
        }

        void ClientLoop(TcpClient client)
        {
            var utf8 = new UTF8Encoding(false);
            try
            {
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, utf8))
                using (var writer = new StreamWriter(stream, utf8) { AutoFlush = true, NewLine = "\n" })
                {
                    string line;
                    while (_running && (line = reader.ReadLine()) != null)
                    {
                        if (line.Trim().Length == 0) continue;
                        // Handle each request on its own worker so a long or stuck command doesn't
                        // block others on this connection (rhino_cancel, status). UI-thread work is
                        // still serialized by Rhino; responses may arrive out of order (match by id).
                        var request = line;
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            bool close;
                            var response = Handle(request, out close);
                            try
                            {
                                lock (writer) writer.WriteLine(response.ToString(Formatting.None));
                                if (close) client.Close();
                            }
                            catch (IOException) { }
                            catch (ObjectDisposedException) { }
                            catch (InvalidOperationException) { }
                        });
                    }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                lock (_clients) _clients.Remove(client);
                try { client.Close(); } catch { }
            }
        }

        JObject Handle(string line, out bool close)
        {
            close = false;
            JToken id = null;
            try
            {
                JObject req;
                try { req = JObject.Parse(line); }
                catch (JsonException ex) { return Error(null, "invalid_params", "Malformed JSON: " + ex.Message, null); }

                id = req["id"];
                var token = req.Value<string>("token");
                if (!ConstantTimeEquals(token, Token))
                {
                    close = true;
                    return Error(id, "unauthorized", "Bad or missing token. Re-read the session file.", null);
                }

                var method = req.Value<string>("method");
                if (string.IsNullOrEmpty(method)) return Error(id, "invalid_params", "Missing 'method'.", null);
                var p = req["params"] as JObject ?? new JObject();

                int timeout = DefaultTimeoutMs;
                var tms = p["timeout_ms"];
                if (tms != null && tms.Type == JTokenType.Integer)
                    timeout = Math.Max(1000, Math.Min(MaxTimeoutMs, tms.Value<int>()));

                Interlocked.Increment(ref _requestCount);
                var result = UiDispatcher.Run(() => Registry.Invoke(method, p), timeout);
                return new JObject
                {
                    ["id"] = id?.DeepClone(),
                    ["ok"] = true,
                    ["result"] = result ?? JValue.CreateNull()
                };
            }
            catch (RpcException ex)
            {
                return Error(id, ex.Code, ex.Message, ex.Details);
            }
            catch (Exception ex)
            {
                var inner = ex is System.Reflection.TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                return Error(id, "internal", inner.GetType().Name + ": " + inner.Message, null);
            }
        }

        static JObject Error(JToken id, string code, string message, JToken details)
        {
            var err = new JObject { ["code"] = code, ["message"] = message ?? code };
            if (details != null) err["details"] = details;
            return new JObject { ["id"] = id?.DeepClone(), ["ok"] = false, ["error"] = err };
        }

        static bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
