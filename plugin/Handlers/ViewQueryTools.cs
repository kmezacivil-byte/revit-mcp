#nullable disable
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMcpPlugin.Handlers
{
    internal static class ViewHelpers
    {
        /// <summary>Vista indicada por view_id, o la vista activa si no se indica.</summary>
        public static View Resolve(ToolArgs args, Document doc)
        {
            if (!args.Has("view_id"))
            {
                if (doc.ActiveView == null) throw new ToolException("No hay una vista activa");
                return doc.ActiveView;
            }
            var v = ToolHelpers.RequireElement(doc, args.RequireLong("view_id")) as View;
            if (v == null) throw new ToolException("El id indicado no corresponde a una vista");
            return v;
        }

        public static Dictionary<long, string> SheetByView(Document doc)
        {
            var map = new Dictionary<long, string>();
            foreach (ViewSheet sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)))
                foreach (var vid in sheet.GetAllPlacedViews())
                    map[vid.GetId()] = sheet.SheetNumber;
            return map;
        }
    }

    public class GetViewsTool : McpTool
    {
        public override string Name => "get_views";
        public override string Description =>
            "Lista las vistas del proyecto (excluye plantillas y vistas internas): id, nombre, tipo, escala, nivel y lámina donde están colocadas.";

        public override JObject InputSchema => Schema.Object(
            ("tipo", "string", "Texto del tipo de vista, p.ej. FloorPlan, Section, ThreeD, Schedule"),
            ("buscar", "string", "Texto que debe contener el nombre de la vista"),
            ("limite", "integer", "Máximo de vistas (100 por defecto)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var tipo = args.Str("tipo");
            var buscar = args.Str("buscar");
            var sheets = ViewHelpers.SheetByView(doc);

            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(ToolHelpers.IsRealView)
                .Where(v => tipo == "" || v.ViewType.ToString().IndexOf(tipo, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(v => buscar == "" || v.Name.IndexOf(buscar, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name)
                .ToList();

            return new
            {
                total = views.Count,
                vistas = views.Take(args.Limit()).Select(v => new
                {
                    id = v.Id.GetId(),
                    nombre = v.Name,
                    tipo = v.ViewType.ToString(),
                    escala = v.Scale,
                    nivel = (v as ViewPlan)?.GenLevel?.Name ?? "",
                    lamina = sheets.TryGetValue(v.Id.GetId(), out var s) ? s : ""
                })
            };
        }
    }

    public class GetViewInfoTool : McpTool
    {
        public override string Name => "get_view_info";
        public override string Description =>
            "Detalle de una vista: tipo, escala, nivel, nivel de detalle, plantilla y cantidad de elementos visibles. " +
            "Sin view_id usa la vista activa.";

        public override JObject InputSchema => Schema.Object(
            ("view_id", "integer", "Id de la vista; vacío = vista activa"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var v = ViewHelpers.Resolve(args, doc);
            var sheets = ViewHelpers.SheetByView(doc);
            var template = v.ViewTemplateId != ElementId.InvalidElementId ? doc.GetElement(v.ViewTemplateId)?.Name : "";

            return new
            {
                id = v.Id.GetId(),
                nombre = v.Name,
                tipo = v.ViewType.ToString(),
                es_vista_activa = doc.ActiveView != null && doc.ActiveView.Id == v.Id,
                escala = v.Scale,
                nivel = (v as ViewPlan)?.GenLevel?.Name ?? "",
                nivel_detalle = v.DetailLevel.ToString(),
                plantilla = template ?? "",
                lamina = sheets.TryGetValue(v.Id.GetId(), out var s) ? s : "",
                elementos_visibles = v.ViewType == ViewType.Schedule
                    ? (int?)null
                    : new FilteredElementCollector(doc, v.Id).WhereElementIsNotElementType().GetElementCount()
            };
        }
    }

    public class GetViewElementsTool : McpTool
    {
        public override string Name => "get_view_elements";
        public override string Description =>
            "Elementos visibles en una vista, con conteo por categoría. Sin view_id usa la vista activa.";

        public override JObject InputSchema => Schema.Object(
            ("view_id", "integer", "Id de la vista; vacío = vista activa"),
            ("categoria", "string", "Alias o nombre de categoría para filtrar"),
            ("limite", "integer", "Máximo de elementos listados (100 por defecto)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var v = ViewHelpers.Resolve(args, doc);
            var categoria = args.Str("categoria");

            var col = new FilteredElementCollector(doc, v.Id).WhereElementIsNotElementType();
            if (categoria != "") col = col.WherePasses(Categories.Filter(doc, categoria));
            var elements = col.ToList();

            return new
            {
                vista = v.Name,
                view_id = v.Id.GetId(),
                total = elements.Count,
                por_categoria = elements.Where(e => e.Category != null)
                    .GroupBy(e => e.Category.Name).OrderByDescending(g => g.Count())
                    .Select(g => new { categoria = g.Key, elementos = g.Count() }),
                devueltos = Math.Min(elements.Count, args.Limit()),
                elementos = elements.Take(args.Limit()).Select(e => ToolHelpers.Brief(doc, e))
            };
        }
    }
}
