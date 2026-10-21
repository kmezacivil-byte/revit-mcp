#nullable disable
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace RevitMcpPlugin
{
    /// <summary>Error esperado de una tool (argumento faltante, elemento inexistente...). Se devuelve como isError.</summary>
    public class ToolException : Exception
    {
        public ToolException(string message) : base(message) { }
    }

    /// <summary>Argumentos de una llamada tools/call, con conversión de tipos tolerante.</summary>
    public sealed class ToolArgs
    {
        private readonly JObject _o;
        public ToolArgs(JObject o) { _o = o ?? new JObject(); }

        public bool Has(string key) => _o[key] != null && _o[key].Type != JTokenType.Null;

        public string Str(string key, string def = "")
        {
            if (!Has(key)) return def;
            var t = _o[key];
            if (t is JValue v) return Convert.ToString(v.Value, CultureInfo.InvariantCulture) ?? def;
            return t.ToString(Formatting.None);
        }

        public string RequireStr(string key)
        {
            var s = Str(key, null);
            if (string.IsNullOrWhiteSpace(s)) throw new ToolException($"Falta el parámetro obligatorio '{key}'");
            return s;
        }

        public double Dbl(string key, double def)
        {
            if (!Has(key)) return def;
            var t = _o[key];
            if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) return (double)t;
            if (double.TryParse((string)t, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
            throw new ToolException($"'{key}' debe ser un número");
        }

        public double RequireDbl(string key)
        {
            if (!Has(key)) throw new ToolException($"Falta el parámetro obligatorio '{key}'");
            return Dbl(key, 0);
        }

        public long Long(string key, long def)
        {
            if (!Has(key)) return def;
            var t = _o[key];
            if (t.Type == JTokenType.Integer) return (long)t;
            if (long.TryParse((string)t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
            throw new ToolException($"'{key}' debe ser un entero");
        }

        public long RequireLong(string key)
        {
            if (!Has(key)) throw new ToolException($"Falta el parámetro obligatorio '{key}'");
            return Long(key, 0);
        }

        public bool Bool(string key, bool def)
        {
            if (!Has(key)) return def;
            var t = _o[key];
            if (t.Type == JTokenType.Boolean) return (bool)t;
            return string.Equals((string)t, "true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Límite de filas devueltas: protege el contexto del modelo de LLM y el tiempo de Revit.</summary>
        public int Limit(string key = "limite", int def = 100, int max = 1000)
        {
            var n = (int)Long(key, def);
            return Math.Max(1, Math.Min(n, max));
        }
    }

    /// <summary>Contexto de ejecución: siempre se crea en el hilo principal de Revit.</summary>
    public sealed class ToolContext
    {
        public UIApplication App { get; }
        public Document Doc { get; }

        public ToolContext(UIApplication app)
        {
            App = app;
            Doc = app.ActiveUIDocument?.Document;
        }

        public Document RequireDoc()
        {
            if (Doc == null) throw new ToolException("No hay documento activo en Revit");
            return Doc;
        }
    }

    /// <summary>
    /// Una tool MCP completa: metadatos para tools/list + implementación para tools/call.
    /// Añadir una tool = crear una clase que herede de esta; el registro la descubre sola.
    /// </summary>
    public abstract class McpTool
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        /// <summary>Si es false, Claude sabe que modifica el modelo.</summary>
        public virtual bool ReadOnly => true;
        public virtual JObject InputSchema => Schema.Object();
        /// <summary>Se ejecuta en el hilo de Revit. Devuelve un objeto serializable.</summary>
        public abstract object Run(ToolArgs args, ToolContext ctx);
    }

    public static class Schema
    {
        public static JObject Object(params (string name, string type, string desc)[] props) =>
            Object(new string[0], props);

        public static JObject Object(string[] required, params (string name, string type, string desc)[] props)
        {
            var p = new JObject();
            foreach (var (name, type, desc) in props)
                p[name] = new JObject { ["type"] = type, ["description"] = desc };
            var o = new JObject { ["type"] = "object", ["properties"] = p };
            if (required.Length > 0) o["required"] = new JArray(required);
            return o;
        }
    }

    /// <summary>Catálogo de tools, descubierto por reflexión y en orden alfabético (tools/list determinista).</summary>
    public static class ToolRegistry
    {
        private static readonly List<McpTool> Tools = Discover();
        private static readonly Dictionary<string, McpTool> ByName =
            Tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        private static Type[] LoadableTypes()
        {
            try { return Assembly.GetExecutingAssembly().GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray(); }
        }

        private static List<McpTool> Discover() =>
            LoadableTypes()
                .Where(t => typeof(McpTool).IsAssignableFrom(t) && !t.IsAbstract)
                .Select(t => (McpTool)Activator.CreateInstance(t))
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToList();

        public static McpTool Find(string name) =>
            name != null && ByName.TryGetValue(name, out var t) ? t : null;

        public static JArray ListJson()
        {
            var arr = new JArray();
            foreach (var t in Tools)
                arr.Add(new JObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["inputSchema"] = t.InputSchema,
                    ["annotations"] = new JObject
                    {
                        ["readOnlyHint"] = t.ReadOnly,
                        ["destructiveHint"] = false,
                        ["idempotentHint"] = t.ReadOnly,
                        ["openWorldHint"] = false
                    }
                });
            return arr;
        }
    }
}
