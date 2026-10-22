#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace RevitMcpPlugin
{
    /// <summary>
    /// Servidor MCP (Streamable HTTP, sin estado) dentro de Revit.
    /// Implementa la spec 2026-07-28 (server/discover, _meta, resultType) y mantiene
    /// compatibilidad con clientes anteriores (initialize, ping).
    /// Solo escucha en 127.0.0.1 y exige "Authorization: Bearer &lt;token&gt;".
    /// </summary>
    public class McpHttpServer
    {
        public const int Port = 5002;
        private const string LatestVersion = "2026-07-28";
        private static readonly string[] SupportedVersions = { "2026-07-28", "2025-11-25", "2025-06-18", "2025-03-26" };

        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMCP");
        private static readonly string LogFile = Path.Combine(DataDir, "RevitMCP_debug.log");
        public static readonly string TokenFile = Path.Combine(DataDir, "token.txt");

        private HttpListener _http;
        private volatile bool _running;
        private string _token;

        private static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
            }
            catch { }
        }

        // El token se conserva entre sesiones para que la configuración del cliente no cambie.
        private static string LoadOrCreateToken()
        {
            Directory.CreateDirectory(DataDir);
            if (File.Exists(TokenFile))
            {
                var existing = File.ReadAllText(TokenFile).Trim();
                if (existing.Length >= 32) return existing;
            }
            var bytes = new byte[32];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            var token = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            File.WriteAllText(TokenFile, token);
            return token;
        }

        private static bool TokenEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public void Start()
        {
            _token = LoadOrCreateToken();
            _running = true;
            try { File.Delete(LogFile); } catch { }

            _http = new HttpListener();
            _http.Prefixes.Add($"http://127.0.0.1:{Port}/mcp/");
            _http.Start();

            new Thread(AcceptLoop) { IsBackground = true, Name = "RevitMCP-Http" }.Start();
            Log($"Servidor MCP en http://127.0.0.1:{Port}/mcp");
        }

        public void Stop()
        {
            _running = false;
            try { _http?.Stop(); } catch { }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var ctx = _http.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { Log($"AcceptLoop: {ex.Message}"); }
            }
        }

        // ── Una petición HTTP ──────────────────────────────────────────────────
        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;

                // Defensa contra DNS rebinding: solo hosts locales, y sin Origin de páginas web.
                var host = req.UserHostName ?? "";
                if (!(host.StartsWith("127.0.0.1") || host.StartsWith("localhost")) ||
                    !string.IsNullOrEmpty(req.Headers["Origin"]))
                { Send(ctx, 403, "Forbidden"); return; }

                var auth = req.Headers["Authorization"] ?? "";
                if (!auth.StartsWith("Bearer ") || !TokenEquals(auth.Substring(7).Trim(), _token))
                {
                    ctx.Response.AddHeader("WWW-Authenticate", "Bearer");
                    Send(ctx, 401, "Unauthorized");
                    return;
                }

                if (req.HttpMethod != "POST") { Send(ctx, 405, "Method Not Allowed"); return; }

                string body;
                using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();

                JObject msg;
                try { msg = JObject.Parse(body); }
                catch { SendJson(ctx, 400, RpcError(null, -32700, "Parse error")); return; }

                var id = msg["id"];
                var method = (string)msg["method"];

                // Notificaciones (sin id): se aceptan sin respuesta.
                if (id == null) { Send(ctx, 202, ""); return; }

                SendJson(ctx, 200, Route(id, method, msg["params"] as JObject));
            }
            catch (Exception ex)
            {
                Log($"Handle: {ex}");
                try { SendJson(ctx, 500, RpcError(null, -32603, ex.Message)); } catch { }
            }
        }

        // ── Enrutado JSON-RPC ──────────────────────────────────────────────────
        private JObject Route(JToken id, string method, JObject p)
        {
            switch (method)
            {
                case "initialize":   // clientes anteriores a 2026-07-28
                    var requested = (string)p?["protocolVersion"];
                    var version = Array.IndexOf(SupportedVersions, requested) >= 0 ? requested : "2025-11-25";
                    return Ok(id, new JObject
                    {
                        ["protocolVersion"] = version,
                        ["capabilities"] = Capabilities(),
                        ["serverInfo"] = ServerInfo()
                    });

                case "server/discover":
                    return Ok(id, new JObject
                    {
                        ["supportedVersions"] = new JArray(SupportedVersions),
                        ["capabilities"] = Capabilities(),
                        ["serverInfo"] = ServerInfo()
                    });

                case "ping":
                    return Ok(id, new JObject());

                case "tools/list":
                    return Ok(id, new JObject
                    {
                        ["tools"] = ToolRegistry.ListJson(),
                        ["ttlMs"] = 10000,
                        ["cacheScope"] = "private"
                    });

                case "tools/call":
                    return CallTool(id, p);

                default:
                    return RpcError(id, -32601, $"Method not found: {method}");
            }
        }

        private static JObject Capabilities() => new JObject { ["tools"] = new JObject { ["listChanged"] = true } };
        private static JObject ServerInfo() => new JObject { ["name"] = "revit-mcp", ["version"] = "2.0.0" };

        private JObject CallTool(JToken id, JObject p)
        {
            var name = (string)p?["name"];
            if (ToolRegistry.Find(name) == null)
                return RpcError(id, -32602, $"Unknown tool: {name}");

            Log($"tools/call {name}");
            var raw = JObject.Parse(RevitDispatcher.Dispatch(name, p["arguments"] as JObject));

            // Los errores de la tool (argumento inválido, elemento inexistente) son isError, no errores de protocolo:
            // así el modelo ve el mensaje y puede corregir la llamada.
            if (!((bool?)raw["ok"] ?? false))
                return Ok(id, new JObject
                {
                    ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = (string)raw["error"] ?? "Error desconocido" }),
                    ["isError"] = true
                });

            var result = raw["result"];
            var response = new JObject
            {
                ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = result?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}" }),
                ["isError"] = false
            };
            if (result is JObject) response["structuredContent"] = result;
            return Ok(id, response);
        }

        // ── Utilidades de respuesta ────────────────────────────────────────────
        private static JObject Ok(JToken id, JObject result)
        {
            result["resultType"] = "complete";
            return new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }

        private static JObject RpcError(JToken id, int code, string message) => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["error"] = new JObject { ["code"] = code, ["message"] = message }
        };

        private static void SendJson(HttpListenerContext ctx, int status, JObject obj)
        {
            ctx.Response.ContentType = "application/json; charset=utf-8";
            Send(ctx, status, obj.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static void Send(HttpListenerContext ctx, int status, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
            // Sin ContentLength64: en pruebas, fijarlo provocaba respuestas vacías (conexión reseteada);
            // con transferencia por fragmentos (chunked) funciona.
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
            ctx.Response.Close();
        }
    }
}
