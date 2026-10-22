#nullable disable
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RevitMcpPlugin.Handlers
{
    public class ExecuteCodeTool : McpTool
    {
        public override string Name => "execute_code";

        public override string Description =>
            "Ejecuta un fragmento de C# dentro de Revit para casos que ninguna otra tool cubre. Disponible solo en Revit 2025 y solo si el " +
            "usuario lo habilitó en la pestaña RevitMCP > Configuración MCP. Variables: Doc (Document), UiDoc, App (UIApplication), Cancel " +
            "(CancellationToken) y Print(x) para escribir salida; el valor de 'return' se devuelve. Usa las unidades internas de Revit " +
            "(pies, radianes). Modos: \"solo_leer\" (por defecto) corre en una transacción que SIEMPRE se revierte, así nada queda " +
            "modificado; \"modificar\" confirma los cambios, exige confirmar=true y que el usuario haya permitido modificar. " +
            "No se puede usar System.IO, Process, red, reflexión, dynamic, ni guardar/cerrar/exportar documentos (hay tools para eso). " +
            "Un bucle que no consulte Cancel no se puede interrumpir y congela Revit. Cada ejecución queda registrada en execute_code.log.";

        public override bool ReadOnly => false;
        public override bool Destructive => true;

        public override string Example =>
            "{\"codigo\":\"var muros = new FilteredElementCollector(Doc).OfClass(typeof(Wall)).GetElementCount(); Print($\\\"Muros: {muros}\\\"); return muros;\",\"modo\":\"solo_leer\"}";

        public override bool Available => McpConfig.ExecuteCodeSupported && McpConfig.ExecuteCodeEnabled;

        public override JObject InputSchema => Schema.Object(new[] { "codigo" },
            ("codigo", "string", "Cuerpo del script en C# (puede usar 'return')"),
            ("modo", "string", "\"solo_leer\" (por defecto, siempre se revierte) o \"modificar\""),
            ("confirmar", "boolean", "Obligatorio con modo=modificar: el usuario aprobó el código"),
            ("timeout_s", "number", "Tiempo límite cooperativo en segundos (20 por defecto, máximo 45)"));

        private static void Log(string text)
        {
            try
            {
                Directory.CreateDirectory(McpHttpServer.DataDir);
                File.AppendAllText(McpConfig.LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
            }
            catch { }
        }

        public override object Run(ToolArgs args, ToolContext ctx)
        {
#if !REVIT2025
            throw new ToolException("execute_code solo está disponible en Revit 2025");
#else
            if (!McpConfig.ExecuteCodeEnabled)
                throw new ToolException("execute_code está desactivada. El usuario debe habilitarla en la pestaña RevitMCP > Configuración MCP");

            var code = args.RequireStr("codigo");
            var modo = args.Str("modo", "solo_leer").ToLowerInvariant();
            if (modo != "solo_leer" && modo != "modificar") throw new ToolException("'modo' debe ser \"solo_leer\" o \"modificar\"");
            var timeout = TimeSpan.FromSeconds(Math.Max(1, Math.Min(args.Dbl("timeout_s", 20), 45)));
            var doc = ctx.RequireDoc();

            // El código se valida SIEMPRE antes de pedir confirmación o de abrir la transacción.
            ScriptRunner.Check(code);

            bool modify = modo == "modificar";
            if (modify)
            {
                if (!McpConfig.ExecuteCodeAllowModify)
                    throw new ToolException("El modo \"modificar\" no está permitido: el usuario debe activarlo en la pestaña RevitMCP > Configuración MCP");
                var preview = code.Length > 600 ? code.Substring(0, 600) + "…" : code;
                args.RequireConfirmation($"Se ejecutará código que MODIFICA el modelo ({code.Split('\n').Length} líneas):\n{preview}\n");
            }

            Log($"modo={modo} timeout={timeout.TotalSeconds:0}s\n{code}\n---");

            var added = new HashSet<long>();
            var changed = new HashSet<long>();
            var deleted = new HashSet<long>();
            EventHandler<DocumentChangedEventArgs> handler = (s, e) =>
            {
                foreach (var id in e.GetAddedElementIds()) added.Add(id.GetId());
                foreach (var id in e.GetModifiedElementIds()) changed.Add(id.GetId());
                foreach (var id in e.GetDeletedElementIds()) deleted.Add(id.GetId());
            };

            var globals = new ScriptGlobals { Doc = doc, UiDoc = ctx.App.ActiveUIDocument, App = ctx.App };
            ScriptOutcome outcome;

            ctx.App.Application.DocumentChanged += handler;
            using (var tx = new Transaction(doc, modify ? "Código MCP" : "Código MCP (solo lectura, se revierte)"))
            {
                try
                {
                    tx.Start();
                    outcome = ScriptRunner.Execute(code, globals, timeout);
                    if (modify) tx.Commit(); else tx.RollBack();
                }
                catch (Exception ex)
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                    Log($"ERROR: {ex.Message}");
                    if (ex is ToolException) throw;
                    if (ex is Autodesk.Revit.Exceptions.ApplicationException)
                        throw new ToolException($"Revit rechazó el código y se revirtió todo: {ex.GetType().Name}: {ex.Message}");
                    throw;
                }
                finally
                {
                    ctx.App.Application.DocumentChanged -= handler;
                }
            }

            Log($"OK {outcome.Seconds}s agregados={added.Count} modificados={changed.Count} borrados={deleted.Count}");
            return new
            {
                modo,
                resultado = outcome.ReturnValue,
                salida = outcome.Output,
                segundos = outcome.Seconds,
                truncado = outcome.Truncated,
                aviso = outcome.Seconds > timeout.TotalSeconds ? "Superó el tiempo límite cooperativo" : null,
                cambios = modify ? new
                {
                    agregados = added.Count,
                    modificados = changed.Count,
                    borrados = deleted.Count,
                    ids_agregados = added.Take(50),
                    ids_modificados = changed.Take(50)
                } : null,
                nota = modify ? "Cambios confirmados; se pueden deshacer con Ctrl+Z" : "Modo solo lectura: cualquier cambio se revirtió"
            };
#endif
        }
    }
}
