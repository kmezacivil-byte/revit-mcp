using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace RevitMcpBridge
{
    /// <summary>
    /// Sin argumentos: puente MCP stdio -> http://127.0.0.1:5002/mcp (una línea JSON-RPC por mensaje).
    /// --install-claude-desktop / --uninstall-claude-desktop: registra o quita el servidor "revit"
    /// en %APPDATA%\Claude\claude_desktop_config.json.
    /// </summary>
    internal static class Program
    {
        // REVITMCP_ENDPOINT permite apuntar a otro servidor (pruebas).
        private static readonly string Endpoint = Environment.GetEnvironmentVariable("REVITMCP_ENDPOINT") ?? "http://127.0.0.1:5002/mcp";
        private const string ServerKey = "revit";

        private static readonly string TokenFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMCP", "token.txt");

        // REVITMCP_CLAUDE_CONFIG permite apuntar a otro archivo (pruebas).
        private static readonly string ClaudeConfig =
            Environment.GetEnvironmentVariable("REVITMCP_CLAUDE_CONFIG") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json");

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        private static int Main(string[] args)
        {
            if (args.Length > 0)
            {
                switch (args[0])
                {
                    // Compatibilidad con instaladores anteriores
                    case "--install-claude-desktop": return Manage(true, "claude-desktop");
                    case "--uninstall-claude-desktop": return Manage(false, "claude-desktop");
                    case "--install": return Manage(true, args.Length > 1 ? args[1] : "all");
                    case "--uninstall": return Manage(false, args.Length > 1 ? args[1] : "all");
                    case "--list": return List();
                    case "--help": case "-h": return Help();
                }
            }
            return RunBridge();
        }

        // ── Puente ──────────────────────────────────────────────────────────────
        private static readonly object OutLock = new object();
        private static StreamWriter _stdout;

        private static void WriteOut(string line)
        {
            lock (OutLock) { _stdout.WriteLine(line); }
        }

        private static int RunBridge()
        {
            var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            _stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

            var watcher = new Thread(WatchToolList) { IsBackground = true, Name = "tool-list-watcher" };
            watcher.Start();

            string line;
            while ((line = stdin.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                var reply = Forward(line);
                if (reply != null) WriteOut(reply);
            }
            return 0;
        }

        // El plugin puede publicar u ocultar herramientas (p.ej. execute_code al activarla en la ventana de Revit), pero los clientes
        // MCP leen la lista una sola vez. Este hilo la consulta cada pocos segundos y, si cambia, envía la notificación estándar
        // notifications/tools/list_changed para que el cliente la vuelva a pedir sin reiniciar.
        private static void WatchToolList()
        {
            const string probe = "{\"jsonrpc\":\"2.0\",\"id\":\"bridge-watch\",\"method\":\"tools/list\"}";
            string baseline = null;
            while (true)
            {
                Thread.Sleep(2000);
                try
                {
                    var reply = Forward(probe);
                    if (Environment.GetEnvironmentVariable("REVITMCP_DEBUG") != null) Console.Error.WriteLine("watch reply: " + (reply ?? "null"));
                    if (reply == null) continue;
                    var root = Json.DeserializeObject(reply) as Dictionary<string, object>;
                    var result = root != null && root.ContainsKey("result") ? root["result"] as Dictionary<string, object> : null;
                    var tools = result != null && result.ContainsKey("tools") ? result["tools"] as IEnumerable : null;   // JavaScriptSerializer devuelve object[]
                    if (tools == null) continue;      // Revit cerrado o plugin sin token: no es un cambio

                    var names = string.Join("|", tools.Cast<object>()
                        .Select(t => ((Dictionary<string, object>)t)["name"] as string).OrderBy(n => n, StringComparer.Ordinal));
                    if (Environment.GetEnvironmentVariable("REVITMCP_DEBUG") != null) Console.Error.WriteLine("watch: " + names);
                    if (baseline == null) { baseline = names; continue; }
                    if (names != baseline)
                    {
                        baseline = names;
                        WriteOut("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}");
                    }
                }
                catch (Exception ex)
                {
                    // el siguiente ciclo reintenta; con REVITMCP_DEBUG se ve el motivo en stderr
                    if (Environment.GetEnvironmentVariable("REVITMCP_DEBUG") != null) Console.Error.WriteLine("watch: " + ex.Message);
                }
            }
        }

        // Devuelve la línea a escribir en stdout, o null si no hay respuesta (notificaciones).
        private static string Forward(string message)
        {
            object id = ExtractId(message);
            try
            {
                var token = File.ReadAllText(TokenFile).Trim();
                var req = (HttpWebRequest)WebRequest.Create(Endpoint);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Accept = "application/json, text/event-stream";
                req.Headers["Authorization"] = "Bearer " + token;
                req.Timeout = 90000;

                var bytes = Encoding.UTF8.GetBytes(message);
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode == HttpStatusCode.Accepted) return null;
                    using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        return r.ReadToEnd().Replace("\r", "").Replace("\n", "");
                }
            }
            catch (FileNotFoundException)
            {
                return ErrorReply(id, "No se encontró el token de RevitMCP. Abre Revit con el plugin instalado.");
            }
            catch (DirectoryNotFoundException)
            {
                return ErrorReply(id, "No se encontró el token de RevitMCP. Abre Revit con el plugin instalado.");
            }
            catch (WebException ex)
            {
                if (ex.Response is HttpWebResponse hr && hr.StatusCode == HttpStatusCode.Unauthorized)
                    return ErrorReply(id, "Token rechazado por el plugin. Reinicia Revit y vuelve a intentar.");
                return ErrorReply(id, "No se puede conectar a Revit. ¿Está abierto con el plugin cargado?");
            }
            catch (Exception ex)
            {
                return ErrorReply(id, ex.Message);
            }
        }

        private static object ExtractId(string message)
        {
            try
            {
                var d = Json.DeserializeObject(message) as Dictionary<string, object>;
                return d != null && d.ContainsKey("id") ? d["id"] : null;
            }
            catch { return null; }
        }

        // Las notificaciones (sin id) no se responden.
        private static string ErrorReply(object id, string text)
        {
            if (id == null) return null;
            return Json.Serialize(new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new Dictionary<string, object> { ["code"] = -32603, ["message"] = text }
            });
        }

        // ── Registro en clientes MCP ────────────────────────────────────────────
        // El puente es un servidor MCP por stdio estándar: cualquier cliente que lance servidores locales sirve.
        // Aquí se automatiza el registro en los más habituales. REVITMCP_TEST_HOME / REVITMCP_TEST_APPDATA permiten probar sin tocar la configuración real.
        private sealed class Client
        {
            public string Name, Description, Path, Container;   // Container: clave JSON ("mcpServers" / "servers"); null = TOML
            public bool Typed;                                   // añade "type":"stdio" (VS Code)
        }

        private static string Home() =>
            Environment.GetEnvironmentVariable("REVITMCP_TEST_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        private static string AppData() =>
            Environment.GetEnvironmentVariable("REVITMCP_TEST_APPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        private static List<Client> Clients() => new List<Client>
        {
            new Client { Name = "claude-desktop", Description = "Claude Desktop", Container = "mcpServers", Path = ClaudeConfig },
            new Client { Name = "gemini-cli", Description = "Gemini CLI (Google)", Container = "mcpServers",
                         Path = System.IO.Path.Combine(Home(), ".gemini", "settings.json") },
            new Client { Name = "cursor", Description = "Cursor", Container = "mcpServers",
                         Path = System.IO.Path.Combine(Home(), ".cursor", "mcp.json") },
            new Client { Name = "vscode", Description = "VS Code (GitHub Copilot)", Container = "servers", Typed = true,
                         Path = System.IO.Path.Combine(AppData(), "Code", "User", "mcp.json") },
            new Client { Name = "codex", Description = "Codex CLI (OpenAI)", Container = null,
                         Path = System.IO.Path.Combine(Home(), ".codex", "config.toml") },
        };

        private static bool Detected(Client c) => Directory.Exists(System.IO.Path.GetDirectoryName(c.Path));

        private static int Help()
        {
            Console.WriteLine("RevitMcpBridge — puente MCP stdio ⇄ Revit");
            Console.WriteLine("  (sin argumentos)         ejecuta el puente (lo lanza el cliente MCP)");
            Console.WriteLine("  --list                   muestra los clientes soportados y si están instalados");
            Console.WriteLine("  --install [cliente|all]  registra el servidor 'revit' (all = los detectados)");
            Console.WriteLine("  --uninstall [cliente|all]");
            Console.WriteLine("Clientes: " + string.Join(", ", Clients().ConvertAll(c => c.Name)));
            return 0;
        }

        private static int List()
        {
            foreach (var c in Clients())
                Console.WriteLine($"{c.Name,-15} {(Detected(c) ? "detectado " : "no detectado")}  {c.Path}");
            return 0;
        }

        private static int Manage(bool install, string which)
        {
            var targets = new List<Client>();
            foreach (var c in Clients())
                if (which == "all" ? Detected(c) : string.Equals(c.Name, which, StringComparison.OrdinalIgnoreCase)) targets.Add(c);
            if (targets.Count == 0)
            {
                Console.Error.WriteLine(which == "all" ? "No se detectó ningún cliente MCP compatible." : $"Cliente desconocido: {which}. Usa --list.");
                return which == "all" ? 0 : 2;
            }

            int failures = 0;
            foreach (var c in targets)
            {
                try
                {
                    if (c.Container == null) EditToml(c, install); else EditJson(c, install);
                    Console.WriteLine($"{(install ? "registrado" : "quitado")}: {c.Description}");
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.Error.WriteLine($"{c.Description}: {ex.Message}");
                }
            }
            return failures == 0 ? 0 : 1;
        }

        private static string ExePath() => System.Reflection.Assembly.GetExecutingAssembly().Location;

        private static void EditJson(Client c, bool install)
        {
            var root = new Dictionary<string, object>();
            if (File.Exists(c.Path))
            {
                var text = File.ReadAllText(c.Path);
                if (text.Trim().Length > 0)
                    root = (Json.DeserializeObject(text) as Dictionary<string, object>) ?? root;
                File.Copy(c.Path, c.Path + ".bak", true);
            }

            var servers = root.ContainsKey(c.Container) ? root[c.Container] as Dictionary<string, object> : null;
            if (servers == null) servers = new Dictionary<string, object>();

            if (install)
            {
                var entry = new Dictionary<string, object>();
                if (c.Typed) entry["type"] = "stdio";
                entry["command"] = ExePath();
                entry["args"] = new ArrayList();
                servers[ServerKey] = entry;
            }
            else servers.Remove(ServerKey);

            root[c.Container] = servers;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(c.Path));
            File.WriteAllText(c.Path, Pretty(Json.Serialize(root)), new UTF8Encoding(false));
        }

        // Codex usa TOML: se reemplaza solo el bloque [mcp_servers.revit] y se conserva el resto del archivo.
        private static void EditToml(Client c, bool install)
        {
            var lines = new List<string>();
            if (File.Exists(c.Path))
            {
                lines.AddRange(File.ReadAllText(c.Path).Replace("\r\n", "\n").Split('\n'));
                File.Copy(c.Path, c.Path + ".bak", true);
            }

            var kept = new List<string>();
            bool skipping = false;
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (t.StartsWith("[")) skipping = t == "[mcp_servers." + ServerKey + "]" || t.StartsWith("[mcp_servers." + ServerKey + ".");
                if (!skipping) kept.Add(line);
            }
            while (kept.Count > 0 && kept[kept.Count - 1].Trim().Length == 0) kept.RemoveAt(kept.Count - 1);

            if (install)
            {
                if (kept.Count > 0) kept.Add("");
                kept.Add("[mcp_servers." + ServerKey + "]");
                kept.Add("command = \"" + ExePath().Replace("\\", "\\\\") + "\"");
                kept.Add("args = []");
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(c.Path));
            File.WriteAllText(c.Path, string.Join("\n", kept) + "\n", new UTF8Encoding(false));
        }

        // Indentación simple para que el archivo siga siendo legible por el usuario.
        private static string Pretty(string json)
        {
            var sb = new StringBuilder();
            int indent = 0;
            bool inStr = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\')) inStr = !inStr;
                if (inStr) { sb.Append(c); continue; }
                switch (c)
                {
                    case '{': case '[':
                        sb.Append(c);
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) break;
                        sb.Append('\n').Append(' ', ++indent * 2);
                        break;
                    case '}': case ']':
                        if (i > 0 && (json[i - 1] == '{' || json[i - 1] == '[')) { sb.Append(c); break; }
                        sb.Append('\n').Append(' ', --indent * 2).Append(c);
                        break;
                    case ',': sb.Append(c).Append('\n').Append(' ', indent * 2); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
