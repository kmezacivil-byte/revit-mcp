#nullable disable
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMcpPlugin.Handlers
{
    public class FilterElementsTool : McpTool
    {
        public override string Name => "filter_elements";
        public override string Description =>
            "Busca elementos combinando filtros: categoría, nombre del tipo, nivel y/o el valor de un parámetro. " +
            "Requiere al menos un filtro.";

        public override JObject InputSchema => Schema.Object(
            ("categoria", "string", "Alias o nombre de categoría"),
            ("tipo", "string", "Texto que debe contener el nombre del tipo"),
            ("nivel", "string", "Nombre exacto del nivel"),
            ("parametro", "string", "Nombre del parámetro a comparar"),
            ("valor", "string", "Valor a comparar con el parámetro"),
            ("operador", "string", "\"contiene\" (por defecto) o \"igual\""),
            ("limite", "integer", "Máximo de elementos (100 por defecto)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var categoria = args.Str("categoria");
            var tipo = args.Str("tipo");
            var nivel = args.Str("nivel");
            var parametro = args.Str("parametro");
            var valor = args.Str("valor");
            bool igual = args.Str("operador", "contiene").Equals("igual", StringComparison.OrdinalIgnoreCase);

            if (categoria == "" && tipo == "" && nivel == "" && parametro == "")
                throw new ToolException("Indica al menos un filtro: categoria, tipo, nivel o parametro");
            if (parametro != "" && valor == "")
                throw new ToolException("Si indicas 'parametro' debes indicar también 'valor'");

            IEnumerable<Element> elements = ToolHelpers.Instances(doc, categoria);
            if (tipo != "")
                elements = elements.Where(e => ToolHelpers.TypeName(doc, e).IndexOf(tipo, StringComparison.OrdinalIgnoreCase) >= 0);
            if (nivel != "")
                elements = elements.Where(e => string.Equals(ToolHelpers.LevelName(doc, e), nivel, StringComparison.OrdinalIgnoreCase));
            if (parametro != "")
                elements = elements.Where(e =>
                {
                    var p = e.LookupParameter(parametro) ?? doc.GetElement(e.GetTypeId())?.LookupParameter(parametro);
                    if (p == null) return false;
                    var text = ToolHelpers.ParamValue(doc, p);
                    return igual
                        ? string.Equals(text, valor, StringComparison.OrdinalIgnoreCase)
                        : text.IndexOf(valor, StringComparison.OrdinalIgnoreCase) >= 0;
                });

            var list = elements.ToList();
            return new
            {
                total = list.Count,
                devueltos = Math.Min(list.Count, args.Limit()),
                elementos = list.Take(args.Limit()).Select(e => ToolHelpers.Brief(doc, e))
            };
        }
    }

    public class GetElementPropertiesTool : McpTool
    {
        public override string Name => "get_element_properties";
        public override string Description =>
            "Propiedades de un elemento por su id: categoría, tipo, nivel, ubicación, caja envolvente (m) y todos sus parámetros. " +
            "Con incluir_tipo=true añade también los parámetros del tipo.";

        public override JObject InputSchema => Schema.Object(new[] { "id" },
            ("id", "integer", "Id del elemento (lo devuelven las demás tools)"),
            ("incluir_tipo", "boolean", "Incluir parámetros del tipo (false por defecto)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var e = ToolHelpers.RequireElement(doc, args.RequireLong("id"));

            return new
            {
                id = e.Id.GetId(),
                nombre = ToolHelpers.SafeName(e),
                categoria = e.Category?.Name ?? "",
                clase = e.GetType().Name,
                tipo = ToolHelpers.TypeName(doc, e),
                nivel = ToolHelpers.LevelName(doc, e),
                ubicacion = Location(e),
                caja_envolvente_m = BoundingBox(e),
                parametros = Parameters(doc, e),
                parametros_tipo = args.Bool("incluir_tipo", false) ? Parameters(doc, doc.GetElement(e.GetTypeId())) : null
            };
        }

        private static object Point(XYZ p) => new { x = Units.M(p.X), y = Units.M(p.Y), z = Units.M(p.Z) };

        private static object Location(Element e)
        {
            var loc = e.Location;
            if (loc is LocationPoint lp) return new { tipo = "punto", punto = Point(lp.Point) };
            if (loc is LocationCurve lc)
                return new
                {
                    tipo = "curva",
                    inicio = Point(lc.Curve.GetEndPoint(0)),
                    fin = Point(lc.Curve.GetEndPoint(1)),
                    longitud_m = Units.M(lc.Curve.Length)
                };
            return null;
        }

        private static object BoundingBox(Element e)
        {
            var bb = e.get_BoundingBox(null);
            return bb == null ? null : new { min = Point(bb.Min), max = Point(bb.Max) };
        }

        private static object Parameters(Document doc, Element e)
        {
            if (e == null) return null;
            var parameters = new List<Parameter>();
            foreach (Parameter p in e.Parameters)
                if (p.Definition != null) parameters.Add(p);

            return parameters
                .OrderBy(p => p.Definition.Name, StringComparer.OrdinalIgnoreCase)
                .Take(300)
                .Select(p => new
                {
                    nombre = p.Definition.Name,
                    valor = ToolHelpers.ParamValue(doc, p),
                    solo_lectura = p.IsReadOnly,
                    almacenamiento = p.StorageType.ToString()
                })
                .ToList();
        }
    }
}
