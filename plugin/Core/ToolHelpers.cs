#nullable disable
using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitMcpPlugin
{
    /// <summary>Unidades internas de Revit (pies) → métricas.</summary>
    public static class Units
    {
        public const double FeetToMeters = 0.3048;
        public const double SqFeetToSqMeters = 0.09290304;
        public const double CuFeetToCuMeters = 0.0283168466;

        public static double M(double feet) => Math.Round(feet * FeetToMeters, 3);
        public static double M2(double sqFeet) => Math.Round(sqFeet * SqFeetToSqMeters, 3);
        public static double M3(double cuFeet) => Math.Round(cuFeet * CuFeetToCuMeters, 3);
    }

    /// <summary>Resolución de categorías por nombre (alias en español/inglés o nombre mostrado en Revit).</summary>
    public static class Categories
    {
        public static readonly Dictionary<string, BuiltInCategory> Aliases =
            new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase)
        {
            { "muros", BuiltInCategory.OST_Walls },           { "walls", BuiltInCategory.OST_Walls },
            { "puertas", BuiltInCategory.OST_Doors },         { "doors", BuiltInCategory.OST_Doors },
            { "ventanas", BuiltInCategory.OST_Windows },      { "windows", BuiltInCategory.OST_Windows },
            { "pisos", BuiltInCategory.OST_Floors },          { "floors", BuiltInCategory.OST_Floors },
            { "techos", BuiltInCategory.OST_Roofs },          { "roofs", BuiltInCategory.OST_Roofs },
            { "columnas", BuiltInCategory.OST_Columns },      { "columns", BuiltInCategory.OST_Columns },
            { "vigas", BuiltInCategory.OST_StructuralFraming },{ "beams", BuiltInCategory.OST_StructuralFraming },
            { "escaleras", BuiltInCategory.OST_Stairs },      { "stairs", BuiltInCategory.OST_Stairs },
            { "mobiliario", BuiltInCategory.OST_Furniture },  { "furniture", BuiltInCategory.OST_Furniture },
            { "tuberias", BuiltInCategory.OST_PipeCurves },   { "pipes", BuiltInCategory.OST_PipeCurves },
            { "ductos", BuiltInCategory.OST_DuctCurves },     { "ducts", BuiltInCategory.OST_DuctCurves },
        };

        /// <summary>Devuelve el filtro de categoría, o lanza ToolException con ayuda si no existe.</summary>
        public static ElementCategoryFilter Filter(Document doc, string name)
        {
            if (Aliases.TryGetValue(name.Trim(), out var bic))
                return new ElementCategoryFilter(bic);

            foreach (Category c in doc.Settings.Categories)
                if (string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                    return new ElementCategoryFilter(c.Id);

            throw new ToolException(
                $"Categoría '{name}' no reconocida. Usa un alias ({string.Join(", ", Aliases.Keys.Take(12))}...) " +
                "o el nombre exacto como aparece en Revit (usa get_categories).");
        }
    }

    public static class ToolHelpers
    {
        public static string SafeName(Element e)
        {
            try { return e.Name ?? ""; } catch { return ""; }
        }

        public static string LevelName(Document doc, Element e)
        {
            try
            {
                if (e.LevelId == null || e.LevelId == ElementId.InvalidElementId) return "";
                return (doc.GetElement(e.LevelId) as Level)?.Name ?? "";
            }
            catch { return ""; }
        }

        public static string TypeName(Document doc, Element e)
        {
            try
            {
                var tid = e.GetTypeId();
                if (tid == null || tid == ElementId.InvalidElementId) return "";
                return doc.GetElement(tid)?.Name ?? "";
            }
            catch { return ""; }
        }

        /// <summary>Resumen común de un elemento, usado por todas las tools de consulta.</summary>
        public static object Brief(Document doc, Element e) => new
        {
            id = e.Id.GetId(),
            nombre = SafeName(e),
            categoria = e.Category?.Name ?? "",
            tipo = TypeName(doc, e),
            nivel = LevelName(doc, e)
        };

        /// <summary>Valor legible de un parámetro (con unidades del proyecto cuando aplica).</summary>
        public static string ParamValue(Document doc, Parameter p)
        {
            try
            {
                if (!p.HasValue) return "";
                var s = p.AsValueString();
                if (!string.IsNullOrEmpty(s)) return s;
                switch (p.StorageType)
                {
                    case StorageType.String:  return p.AsString() ?? "";
                    case StorageType.Integer: return p.AsInteger().ToString(CultureInfo.InvariantCulture);
                    case StorageType.Double:  return p.AsDouble().ToString("0.####", CultureInfo.InvariantCulture);
                    case StorageType.ElementId:
                        var id = p.AsElementId();
                        return id == ElementId.InvalidElementId ? "" : (SafeName(doc.GetElement(id)) ?? "");
                    default: return "";
                }
            }
            catch { return ""; }
        }

        /// <summary>Id de elemento recibido por JSON → ElementId, según la versión de Revit.</summary>
        public static ElementId ToElementId(long id)
        {
#if REVIT_PRE2024
            return new ElementId((int)id);
#else
            return new ElementId(id);
#endif
        }

        /// <summary>Ejecuta una modificación en una transacción con nombre (Ctrl+Z la deshace); revierte si falla.</summary>
        public static T InTransaction<T>(Document doc, string name, Func<T> action)
        {
            using (var tx = new Transaction(doc, name + " (MCP)"))
            {
                tx.Start();
                try
                {
                    var result = action();
                    tx.Commit();
                    return result;
                }
                catch (Exception ex)
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                    if (ex is ToolException) throw;
                    // Las excepciones de la API de Revit traen mensajes crípticos: se envuelven con la operación que falló.
                    if (ex is Autodesk.Revit.Exceptions.ApplicationException)
                        throw new ToolException($"Revit rechazó la operación «{name}» y se revirtió por completo: {ex.GetType().Name}: {ex.Message}");
                    throw;
                }
            }
        }

        public static Level FindLevel(Document doc, string name)
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            var level = string.IsNullOrWhiteSpace(name)
                ? levels.OrderBy(l => l.Elevation).FirstOrDefault()
                : levels.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (level == null) throw new ToolException(string.IsNullOrWhiteSpace(name)
                ? "El proyecto no tiene niveles" : $"Nivel '{name}' no encontrado (usa get_levels)");
            return level;
        }

        public static T FindType<T>(Document doc, string name, string kind) where T : Element
        {
            var types = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().ToList();
            var found = string.IsNullOrWhiteSpace(name)
                ? types.FirstOrDefault()
                : types.FirstOrDefault(t => SafeName(t).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (found == null) throw new ToolException(string.IsNullOrWhiteSpace(name)
                ? $"El proyecto no tiene {kind}" : $"{kind} '{name}' no encontrado. Disponibles: " +
                  string.Join(", ", types.Take(15).Select(SafeName)));
            return found;
        }

        public static Element RequireElement(Document doc, long id)
        {
            var e = doc.GetElement(ToElementId(id));
            if (e == null) throw new ToolException($"No existe un elemento con id {id}");
            return e;
        }

        public static FilteredElementCollector Instances(Document doc, string categoria)
        {
            var col = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            if (!string.IsNullOrWhiteSpace(categoria)) col = col.WherePasses(Categories.Filter(doc, categoria));
            return col;
        }

        public static bool IsRealView(View v) =>
            v != null && !v.IsTemplate &&
            v.ViewType != ViewType.ProjectBrowser && v.ViewType != ViewType.SystemBrowser &&
            v.ViewType != ViewType.Internal && v.ViewType != ViewType.Undefined;
    }
}
