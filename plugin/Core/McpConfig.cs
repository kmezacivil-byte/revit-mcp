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
    /// { "ejecutar_codigo": { "habilitado": true, "permitir_modificar": false } }
    /// </summary>
    internal static class McpConfig
    {
        public static string FilePath => Path.Combine(McpHttpServer.DataDir, "config.json");
        public static string LogPath => Path.Combine(McpHttpServer.DataDir, "execute_code.log");

        /// <summary>execute_code usa Roslyn y solo se distribuye para Revit 2025 (.NET 8).</summary>
        public static bool ExecuteCodeSupported
        {
            get
            {
#if REVIT2025
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
            catch { /* un config.json roto equivale a "todo desactivado" */ }
            return new JObject();
        }

        private static bool Flag(string section, string key)
        {
            var s = Read()[section] as JObject;
            var t = s?[key];
            return t != null && t.Type == JTokenType.Boolean && (bool)t;
        }

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
            Directory.CreateDirectory(McpHttpServer.DataDir);
            File.WriteAllText(FilePath, root.ToString());
        }
    }
}
