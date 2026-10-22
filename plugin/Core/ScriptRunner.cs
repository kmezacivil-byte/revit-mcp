#if REVIT2025
#nullable disable
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Scripting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace RevitMcpPlugin
{
    /// <summary>Variables que ve el código del usuario: Doc, UiDoc, App, Cancel y Print().</summary>
    public sealed class ScriptGlobals
    {
        public Document Doc;
        public UIDocument UiDoc;
        public UIApplication App;
        public CancellationToken Cancel;
        public readonly List<string> Output = new List<string>();

        /// <summary>Escribe una línea en la salida que se devuelve al modelo.</summary>
        public void Print(object value) => Output.Add(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
    }

    public sealed class ScriptOutcome
    {
        public JToken ReturnValue;
        public List<string> Output;
        public double Seconds;
        public bool Truncated;
    }

    /// <summary>
    /// Compila y ejecuta código C# (Roslyn). Las comprobaciones de seguridad son una RED DE SEGURIDAD contra errores y usos
    /// obvios, no un sandbox: no impiden que código hostil encuentre otro camino. Por eso la tool exige que el usuario la habilite.
    /// </summary>
    public static class ScriptRunner
    {
        private const int MaxItems = 500;
        private const int MaxChars = 50000;

        // Espacios de nombres y tipos completos que el código no puede usar.
        private static readonly string[] BlockedNamespaces =
        {
            "System.IO", "System.Diagnostics", "System.Net", "System.Reflection", "System.Runtime.InteropServices",
            "System.Runtime.Loader", "System.Security", "System.CodeDom", "Microsoft.Win32", "Microsoft.CodeAnalysis",
            "System.Linq.Expressions"
        };

        private static readonly string[] BlockedTypes =
        {
            "System.Environment", "System.AppDomain", "System.Activator", "System.Threading.Thread", "System.Threading.ThreadPool",
            "System.Threading.Timer", "System.Threading.Process", "Autodesk.Revit.UI.TaskDialog"
        };

        // Miembros de Revit que guardan, cierran, exportan o abren documentos: hay tools dedicadas con confirmación.
        private static readonly Dictionary<string, string[]> BlockedMembers = new Dictionary<string, string[]>
        {
            { "Autodesk.Revit.DB.Document", new[] { "Save", "Close", "Export", "Synchronize", "Reload" } },
            { "Autodesk.Revit.ApplicationServices.Application", new[] { "Open", "NewProject", "NewFamily", "Close" } },
            { "Autodesk.Revit.UI.UIApplication", new[] { "Open" } },
            { "Autodesk.Revit.UI.UIDocument", new[] { "Save" } },
            // Reflexión indirecta a través de System.Type
            { "System.Type", new[] { "GetMethod", "GetProperty", "GetField", "GetMember", "GetConstructor", "InvokeMember",
                                      "Assembly", "Module", "GetEvent", "GetNestedType", "MakeGenericType", "GetType", "GetInterface" } },
        };

        /// <summary>Ruta de un ensamblado, o null si no se puede cargar (p.ej. fuera de Revit, en pruebas).</summary>
        private static string Location(Func<Assembly> assembly)
        {
            try { return assembly().Location; }
            catch { return null; }
        }

        /// <summary>RevitAPIUI: si no se puede cargar (pruebas fuera de Revit), basta su ruta junto a RevitAPI para referenciarla.</summary>
        private static string RevitUiLocation()
        {
            var loaded = Location(() => typeof(UIApplication).Assembly);
            if (loaded != null) return loaded;
            var api = Location(() => typeof(Document).Assembly);
            return api == null ? null : Path.Combine(Path.GetDirectoryName(api), "RevitAPIUI.dll");
        }

        private static List<MetadataReference> References()
        {
            string runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
            var assemblies = new List<string>
            {
                typeof(object).Assembly.Location,                      // System.Private.CoreLib
                Path.Combine(runtimeDir, "System.Runtime.dll"),        // fachada que referencian RevitAPI y Newtonsoft
                Path.Combine(runtimeDir, "netstandard.dll"),
                typeof(Enumerable).Assembly.Location,                  // System.Linq
                typeof(List<>).Assembly.Location,                      // System.Collections
                typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
                Location(() => typeof(Document).Assembly),             // RevitAPI
                RevitUiLocation(),                                     // RevitAPIUI
                Location(() => typeof(JObject).Assembly),              // Newtonsoft.Json
                Location(() => typeof(ScriptGlobals).Assembly),        // para el tipo de los globales
            };
            // Sin Microsoft.CSharp: 'dynamic' no compila (evita saltarse las comprobaciones con enlace dinámico).
            return assemblies.Where(a => a != null && File.Exists(a)).Distinct().Select(a => (MetadataReference)MetadataReference.CreateFromFile(a)).ToList();
        }

        private static ScriptOptions Options() => ScriptOptions.Default
            .WithReferences(References())
            .WithImports("System", "System.Linq", "System.Collections.Generic", "System.Text", "System.Globalization",
                         "Autodesk.Revit.DB", "Autodesk.Revit.UI", "Newtonsoft.Json.Linq")
            .WithEmitDebugInformation(false);

        // ── Comprobaciones previas ─────────────────────────────────────────────
        private static string TypeFullName(ITypeSymbol t) => t?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat.WithGenericsOptions(SymbolDisplayGenericsOptions.None));

        private static string BlockedReason(ISymbol symbol)
        {
            if (symbol == null || symbol is INamespaceSymbol) return null;
            var type = symbol as ITypeSymbol ?? symbol.ContainingType;
            var ns = (symbol as ITypeSymbol)?.ContainingNamespace ?? symbol.ContainingNamespace;

            var nsName = ns?.ToDisplayString() ?? "";
            foreach (var b in BlockedNamespaces)
                if (nsName == b || nsName.StartsWith(b + ".")) return $"usa {symbol.Name} ({nsName}), un espacio de nombres no permitido";
            if ((nsName == "RevitMcpPlugin" || nsName.StartsWith("RevitMcpPlugin.")) && type?.Name != "ScriptGlobals") return "accede a componentes internos del plugin";

            var typeName = TypeFullName(type);
            if (typeName != null && BlockedTypes.Contains(typeName)) return $"usa {typeName}, que no está permitido";
            if (typeName != null && BlockedMembers.TryGetValue(typeName, out var prefixes) && !(symbol is ITypeSymbol))
                if (prefixes.Any(p => symbol.Name.StartsWith(p, StringComparison.Ordinal)))
                    return $"usa {typeName}.{symbol.Name}, que no está permitido (para eso hay tools con confirmación)";
            return null;
        }

        private static List<string> Violations(Compilation compilation)
        {
            var found = new List<string>();
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();
                foreach (var node in root.DescendantNodes())
                {
                    if (!(node is IdentifierNameSyntax) && !(node is GenericNameSyntax)) continue;
                    var reason = BlockedReason(model.GetSymbolInfo(node).Symbol);
                    if (reason != null)
                    {
                        var line = tree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
                        found.Add($"línea {line}: {reason}");
                    }
                }

                // Bucles infinitos que no consultan Cancel: congelarían Revit (no se pueden interrumpir desde fuera).
                bool mentionsCancel = root.DescendantNodes().OfType<IdentifierNameSyntax>().Any(i => i.Identifier.Text == "Cancel");
                if (!mentionsCancel)
                    foreach (var node in root.DescendantNodes())
                    {
                        bool infinite = (node is WhileStatementSyntax w && w.Condition.IsKind(SyntaxKind.TrueLiteralExpression)) ||
                                        (node is DoStatementSyntax d && d.Condition.IsKind(SyntaxKind.TrueLiteralExpression)) ||
                                        (node is ForStatementSyntax f && f.Condition == null);
                        if (infinite)
                            found.Add($"línea {tree.GetLineSpan(node.Span).StartLinePosition.Line + 1}: bucle infinito sin comprobar 'Cancel' (congelaría Revit); usa Cancel.IsCancellationRequested");
                    }
            }
            return found.Distinct().Take(15).ToList();
        }

        // ── Conversión del resultado ───────────────────────────────────────────
        private static JToken ToJson(object v, int depth, ref bool truncated)
        {
            if (v == null) return JValue.CreateNull();
            switch (v)
            {
                case string s: return s;
                case bool b: return b;
                case int _: case long _: case double _: case float _: case decimal _: case short _: case byte _:
                    return JToken.FromObject(v);
                case Enum e: return e.ToString();
                case ElementId id: return id.GetId();
                case XYZ p: return new JObject { ["x"] = p.X, ["y"] = p.Y, ["z"] = p.Z };
                case Element el:
                    return new JObject { ["id"] = el.Id.GetId(), ["nombre"] = ToolHelpers.SafeName(el), ["categoria"] = el.Category?.Name ?? "", ["clase"] = el.GetType().Name };
            }
            if (depth >= 3) return Convert.ToString(v, CultureInfo.InvariantCulture);

            if (v is IDictionary dict)
            {
                var o = new JObject();
                foreach (DictionaryEntry kv in dict) o[Convert.ToString(kv.Key, CultureInfo.InvariantCulture)] = ToJson(kv.Value, depth + 1, ref truncated);
                return o;
            }
            if (v is IEnumerable seq)
            {
                var arr = new JArray();
                foreach (var item in seq)
                {
                    if (arr.Count >= MaxItems) { truncated = true; break; }
                    arr.Add(ToJson(item, depth + 1, ref truncated));
                }
                return arr;
            }
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        // ── Ejecución ──────────────────────────────────────────────────────────
        /// <summary>Solo valida: compila y revisa las restricciones. Lanza ToolException con la lista de problemas.</summary>
        public static void Check(string code)
        {
            Script<object> script = CSharpScript.Create<object>(code, Options(), typeof(ScriptGlobals));
            var compilation = script.GetCompilation();
            var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Take(10).ToList();
            if (errors.Count > 0)
                throw new ToolException("El código no compila:\n- " + string.Join("\n- ", errors.Select(d =>
                    $"línea {d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage(CultureInfo.InvariantCulture)} ({d.Id})")));
            var violations = Violations(compilation);
            if (violations.Count > 0)
                throw new ToolException("El código usa funciones no permitidas:\n- " + string.Join("\n- ", violations));
        }

        public static ScriptOutcome Execute(string code, ScriptGlobals globals, TimeSpan timeout)
        {
            Check(code);
            var script = CSharpScript.Create<object>(code, Options(), typeof(ScriptGlobals));
            var clock = Stopwatch.StartNew();
            using (var cts = new CancellationTokenSource(timeout))
            {
                globals.Cancel = cts.Token;
                ScriptState<object> state;
                try { state = script.RunAsync(globals, cts.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { throw new ToolException($"El código se canceló al superar el tiempo límite de {timeout.TotalSeconds:0} s"); }
                catch (Exception ex) when (!(ex is ToolException))
                {
                    var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                    throw new ToolException($"El código lanzó {inner.GetType().Name}: {inner.Message}");
                }

                bool truncated = false;
                var value = ToJson(state.ReturnValue, 0, ref truncated);
                var text = value.ToString(Newtonsoft.Json.Formatting.None);
                if (text.Length > MaxChars) { value = text.Substring(0, MaxChars) + "…"; truncated = true; }
                var output = globals.Output.Take(MaxItems).ToList();
                if (globals.Output.Count > MaxItems) truncated = true;
                return new ScriptOutcome { ReturnValue = value, Output = output, Seconds = Math.Round(clock.Elapsed.TotalSeconds, 2), Truncated = truncated };
            }
        }
    }
}
#endif
