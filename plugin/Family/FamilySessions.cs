#nullable disable
using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMcpPlugin.Families
{
    /// <summary>Una familia abierta por el plugin (documento en segundo plano, sin ventana).</summary>
    public sealed class FamilySession
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public Document Doc { get; set; }
        public string Template { get; set; }
        /// <summary>Piezas creadas por nombre (extrusiones, barridos, instancias anidadas...).</summary>
        public Dictionary<string, FormInfo> Forms { get; } = new Dictionary<string, FormInfo>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Pieza creada en la familia: su id y el eje de extrusión (X, Y o Z; ' ' si no aplica).</summary>
    public sealed class FormInfo
    {
        public ElementId Id { get; set; }
        public char Axis { get; set; } = ' ';
        public string Kind { get; set; }
    }

    /// <summary>
    /// Registro de familias abiertas entre llamadas. El 'familia_doc_id' que ven las tools es un entero de sesión:
    /// no es un ElementId y deja de ser válido al reiniciar Revit.
    /// Solo se usa desde el hilo de Revit (las tools se ejecutan ahí), así que no necesita bloqueo.
    /// </summary>
    public static class FamilySessions
    {
        private static readonly Dictionary<int, FamilySession> Sessions = new Dictionary<int, FamilySession>();
        private static int _next = 1;

        public static FamilySession Add(Document doc, string name, string template)
        {
            var s = new FamilySession { Id = _next++, Doc = doc, Name = name, Template = template };
            Sessions[s.Id] = s;
            return s;
        }

        public static FamilySession Require(ToolArgs args, string key = "familia_doc_id")
        {
            var id = (int)args.RequireLong(key);
            if (!Sessions.TryGetValue(id, out var s))
                throw new ToolException($"No hay una familia abierta con familia_doc_id {id}. Usa list_open_families o create_family");
            if (!IsAlive(s.Doc))
            {
                Sessions.Remove(id);
                throw new ToolException($"La familia {id} ya no está abierta en Revit (se cerró manualmente). Créala o ábrela de nuevo");
            }
            return s;
        }

        public static IEnumerable<FamilySession> All()
        {
            foreach (var dead in Sessions.Where(kv => !IsAlive(kv.Value.Doc)).Select(kv => kv.Key).ToList())
                Sessions.Remove(dead);
            return Sessions.Values.OrderBy(s => s.Id).ToList();
        }

        public static void Remove(int id) => Sessions.Remove(id);

        /// <summary>Cierra sin guardar todas las familias abiertas (al cerrar Revit o descargar el plugin).</summary>
        public static void CloseAll()
        {
            foreach (var s in Sessions.Values.ToList())
            {
                try { if (IsAlive(s.Doc)) s.Doc.Close(false); } catch { }
            }
            Sessions.Clear();
        }

        private static bool IsAlive(Document doc)
        {
            try { return doc != null && doc.IsValidObject && !string.IsNullOrEmpty(doc.Title); }
            catch { return false; }
        }
    }
}
