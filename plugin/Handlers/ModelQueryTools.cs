#nullable disable
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMcpPlugin.Handlers
{
    /// <summary>Conteos por categoría compartidos por get_categories y get_model_statistics.</summary>
    internal static class CategoryCounts
    {
        public static Dictionary<string, int> Count(Document doc, bool includeAnnotation)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                var c = e.Category;
                if (c == null) continue;
                if (!includeAnnotation && c.CategoryType != CategoryType.Model) continue;
                counts[c.Name] = counts.TryGetValue(c.Name, out var n) ? n + 1 : 1;
            }
            return counts;
        }
    }

    public class GetSelectionTool : McpTool
    {
        public override string Name => "get_selection";
        public override string Description =>
            "Elementos seleccionados actualmente en Revit por el usuario (id, nombre, categoría, tipo y nivel).";

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var ids = ctx.App.ActiveUIDocument.Selection.GetElementIds().ToList();
            var elements = ids.Select(id => doc.GetElement(id)).Where(e => e != null).ToList();
            return new
            {
                total = elements.Count,
                devueltos = Math.Min(elements.Count, 200),
                elementos = elements.Take(200).Select(e => ToolHelpers.Brief(doc, e))
            };
        }
    }

    public class GetCategoriesTool : McpTool
    {
        public override string Name => "get_categories";
        public override string Description =>
            "Categorías con elementos en el proyecto y cuántos hay en cada una, de mayor a menor. " +
            "Úsala para descubrir el nombre exacto de una categoría antes de consultarla.";

        public override JObject InputSchema => Schema.Object(
            ("buscar", "string", "Texto que debe contener el nombre de la categoría"),
            ("incluir_anotacion", "boolean", "Incluir categorías de anotación (false por defecto)"),
            ("limite", "integer", "Máximo de categorías (100 por defecto)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var buscar = args.Str("buscar");
            var counts = CategoryCounts.Count(doc, args.Bool("incluir_anotacion", false))
                .Where(kv => buscar == "" || kv.Key.IndexOf(buscar, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(kv => kv.Value)
                .ToList();

            return new
            {
                total_categorias = counts.Count,
                categorias = counts.Take(args.Limit()).Select(kv => new { nombre = kv.Key, elementos = kv.Value })
            };
        }
    }

    public class GetFamiliesTool : McpTool
    {
        public override string Name => "get_families";
        public override string Description =>
            "Familias cargadas en el proyecto con sus tipos disponibles. Filtra por categoría y/o texto.";

        public override JObject InputSchema => Schema.Object(
            ("categoria", "string", "Alias o nombre de categoría, p.ej. \"puertas\""),
            ("buscar", "string", "Texto que debe contener el nombre de la familia o del tipo"),
            ("limite", "integer", "Máximo de familias (100 por defecto)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var categoria = args.Str("categoria");
            var buscar = args.Str("buscar");

            var col = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol));
            if (categoria != "") col = col.WherePasses(Categories.Filter(doc, categoria));

            var families = col.Cast<FamilySymbol>()
                .Where(s => buscar == "" ||
                            (s.FamilyName ?? "").IndexOf(buscar, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            ToolHelpers.SafeName(s).IndexOf(buscar, StringComparison.OrdinalIgnoreCase) >= 0)
                .GroupBy(s => new { familia = s.FamilyName ?? "", categoria = s.Category?.Name ?? "" })
                .OrderBy(g => g.Key.categoria).ThenBy(g => g.Key.familia)
                .ToList();

            return new
            {
                total_familias = families.Count,
                familias = families.Take(args.Limit()).Select(g => new
                {
                    familia = g.Key.familia,
                    categoria = g.Key.categoria,
                    tipos = g.Select(s => ToolHelpers.SafeName(s)).OrderBy(n => n).ToList()
                })
            };
        }
    }

    public class GetModelStatisticsTool : McpTool
    {
        public override string Name => "get_model_statistics";
        public override string Description =>
            "Resumen del modelo: total de elementos, categorías principales, niveles, vistas, láminas, ambientes, " +
            "familias y vínculos. Puede tardar en modelos muy grandes.";

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var counts = CategoryCounts.Count(doc, false);

            int Count(Type t) => new FilteredElementCollector(doc).OfClass(t).GetElementCount();

            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(ToolHelpers.IsRealView).ToList();

            return new
            {
                titulo = doc.Title,
                colaborativo = doc.IsWorkshared,
                elementos_de_modelo = counts.Values.Sum(),
                categorias_principales = counts.OrderByDescending(kv => kv.Value).Take(15)
                    .Select(kv => new { nombre = kv.Key, elementos = kv.Value }),
                niveles = Count(typeof(Level)),
                vistas = views.Count,
                vistas_por_tipo = views.GroupBy(v => v.ViewType.ToString())
                    .OrderByDescending(g => g.Count()).Select(g => new { tipo = g.Key, total = g.Count() }),
                laminas = Count(typeof(ViewSheet)),
                ambientes = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType().GetElementCount(),
                familias_cargadas = Count(typeof(Family)),
                tipos_de_familia = Count(typeof(FamilySymbol)),
                vinculos_revit = Count(typeof(RevitLinkInstance))
            };
        }
    }

    public class GetMaterialQuantitiesTool : McpTool
    {
        public override string Name => "get_material_quantities";
        public override string Description =>
            "Cantidades por material (área m² y volumen m³) sumando los elementos del modelo. " +
            "Filtra por categoría para acotar el cálculo; sin filtro recorre todo el modelo y puede tardar.";

        public override JObject InputSchema => Schema.Object(
            ("categoria", "string", "Alias o nombre de categoría, p.ej. \"muros\""),
            ("limite", "integer", "Máximo de materiales (50 por defecto)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var area = new Dictionary<string, double>();
            var volume = new Dictionary<string, double>();
            var elementCount = new Dictionary<string, int>();
            int analyzed = 0;

            foreach (var e in ToolHelpers.Instances(doc, args.Str("categoria")))
            {
                ICollection<ElementId> materialIds;
                try { materialIds = e.GetMaterialIds(false); }
                catch { continue; }
                if (materialIds == null || materialIds.Count == 0) continue;

                analyzed++;
                foreach (var mid in materialIds)
                {
                    var name = ToolHelpers.SafeName(doc.GetElement(mid));
                    if (name == "") continue;
                    try
                    {
                        area[name] = (area.TryGetValue(name, out var a) ? a : 0) + e.GetMaterialArea(mid, false);
                        volume[name] = (volume.TryGetValue(name, out var v) ? v : 0) + e.GetMaterialVolume(mid);
                        elementCount[name] = (elementCount.TryGetValue(name, out var n) ? n : 0) + 1;
                    }
                    catch { }
                }
            }

            var rows = area.Keys.OrderByDescending(k => volume[k]).ToList();
            return new
            {
                elementos_analizados = analyzed,
                total_materiales = rows.Count,
                materiales = rows.Take(args.Limit("limite", 50)).Select(k => new
                {
                    material = k,
                    area_m2 = Units.M2(area[k]),
                    volumen_m3 = Units.M3(volume[k]),
                    elementos = elementCount[k]
                })
            };
        }
    }
}
