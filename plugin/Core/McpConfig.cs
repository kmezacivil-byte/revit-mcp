#nullable disable
using Newtonsoft.Json.Linq;
using System;
using System.IO;

namespace RevitMcpPlugin
{
    /// <summary>
    /// Configuración local en %LOCALAPPDATA%\RevitMCP\config.json. La cambia el usuario desde la ventana
    /// "Configuración MCP" de la cinta de Revit (o a mano): ninguna tool MCP la modifica, así que un modelo de IA
    /// no puede activar por sí mismo funciones sensibles. Formato:
    /// {
    ///   "perfil_herramientas": "completo",                                   // completo | modelado | familias | solo_lectura
    ///   "ejecutar_codigo": { "habilitado": false, "permitir_modificar": false },
    ///   "acceso_remoto":   { "habilitado": false, "solo_lectura": true }     // para ChatGPT u otros clientes en la nube (vía túnel)
    /// }
    /// </summary>
    internal static class McpConfig
    {
        public static string FilePath => Path.Combine(McpHttpServer.DataDir, "config.json");
        public static string LogPath => Path.Combine(McpHttpServer.DataDir, "execute_code.log");
        public static string RemoteTokenFile => Path.Combine(McpHttpServer.DataDir, "remote_token.txt");

        /// <summary>execute_code usa Roslyn y solo se distribuye para Revit 2025 o superior (.NET 8+).</summary>
        public static bool ExecuteCodeSupported
        {
            get
            {
#if REVIT_MODERN
                return true;
#else
                return false;
#endif
            }
        }

        private static JObject Read()
        {
            try
            {
                if (File.Exists(FilePath)) return JObject.Parse(File.ReadAllText(FilePath));
            }
            catch { /* un config.json roto equivale a la configuración por defecto (todo lo sensible desactivado) */ }
            return new JObject();
        }

        private static void Write(JObject root)
        {
            Directory.CreateDirectory(McpHttpServer.DataDir);
            File.WriteAllText(FilePath, root.ToString());
        }

        private static bool Flag(string section, string key, bool defaultValue = false)
        {
            var s = Read()[section] as JObject;
            var t = s?[key];
            return t != null && t.Type == JTokenType.Boolean ? (bool)t : defaultValue;
        }

        // ── execute_code ───────────────────────────────────────────────────────
        public static bool ExecuteCodeEnabled => Flag("ejecutar_codigo", "habilitado");
        public static bool ExecuteCodeAllowModify => ExecuteCodeEnabled && Flag("ejecutar_codigo", "permitir_modificar");

        /// <summary>Guarda la configuración de execute_code conservando el resto del archivo. Permitir modificar exige habilitar.</summary>
        public static void SaveExecuteCode(bool enabled, bool allowModify)
        {
            var root = Read();
            var section = root["ejecutar_codigo"] as JObject ?? new JObject();
            section["habilitado"] = enabled;
            section["permitir_modificar"] = enabled && allowModify;
            root["ejecutar_codigo"] = section;
            Write(root);
        }

        // ── Autenticación (Supabase) ───────────────────────────────────────────
        /// <summary>Por defecto true: las herramientas (salvo ping) exigen iniciar sesión con la cuenta DJP. REVITMCP_AUTH=0 lo desactiva en pruebas.</summary>
        public static bool AuthRequired
        {
            get
            {
                if (Environment.GetEnvironmentVariable("REVITMCP_AUTH") == "0") return false;
                return Flag("autenticacion", "requerida", true);
            }
        }

        public static void SaveAuthRequired(bool required)
        {
            var root = Read();
            var section = root["autenticacion"] as JObject ?? new JObject();
            section["requerida"] = required;
            root["autenticacion"] = section;
            Write(root);
        }

        // ── Perfil de herramientas ─────────────────────────────────────────────
        public static string Profile
        {
            get
            {
                var p = (string)Read()["perfil_herramientas"];
                return Array.IndexOf(ToolProfiles.Names, p) >= 0 ? p : "completo";
            }
        }

        public static void SaveProfile(string profile)
        {
            var root = Read();
            root["perfil_herramientas"] = Array.IndexOf(ToolProfiles.Names, profile) >= 0 ? profile : "completo";
            Write(root);
        }

        // ── Acceso remoto (ChatGPT, API de OpenAI/Gemini con servidores MCP remotos) ─
        public static bool RemoteEnabled => Flag("acceso_remoto", "habilitado");
        /// <summary>Por defecto true: el acceso remoto solo debe poder leer salvo que el usuario diga lo contrario.</summary>
        public static bool RemoteReadOnly => Flag("acceso_remoto", "solo_lectura", true);

        /// <summary>Dirección pública del túnel (https://algo.trycloudflare.com), sin ruta.</summary>
        public static string PublicUrl => (string)(Read()["acceso_remoto"] as JObject)?["url_publica"] ?? "";
        public static string CloudflaredPath => (string)(Read()["acceso_remoto"] as JObject)?["ruta_cloudflared"] ?? "";

        public static void SaveRemote(bool enabled, bool readOnly, string publicUrl = null, string cloudflaredPath = null)
        {
            var root = Read();
            var section = root["acceso_remoto"] as JObject ?? new JObject();
            section["habilitado"] = enabled;
            section["solo_lectura"] = readOnly;
            if (publicUrl != null) section["url_publica"] = publicUrl;
            if (cloudflaredPath != null) section["ruta_cloudflared"] = cloudflaredPath;
            root["acceso_remoto"] = section;
            Write(root);
        }

        /// <summary>Token de la URL remota (/mcp/&lt;token&gt;). Distinto del token local: revocar uno no afecta al otro.</summary>
        public static string RemoteToken()
        {
            try
            {
                if (File.Exists(RemoteTokenFile))
                {
                    var t = File.ReadAllText(RemoteTokenFile).Trim();
                    if (t.Length >= 32) return t;
                }
            }
            catch { }
            return RegenerateRemoteToken();
        }

        public static string RegenerateRemoteToken()
        {
            var bytes = new byte[32];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var token = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            Directory.CreateDirectory(McpHttpServer.DataDir);
            File.WriteAllText(RemoteTokenFile, token);
            return token;
        }
    }
}
