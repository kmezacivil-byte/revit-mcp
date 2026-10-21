#nullable disable
using Newtonsoft.Json.Linq;
using System.Linq;

namespace RevitMcpPlugin.Handlers
{
    public class GetElementsByCategoryTool : McpTool
    {
        public override string Name => "get_elements_by_category";
        public override string Description =>
            "Elementos de una categoría. Alias: muros/walls, puertas/doors, ventanas/windows, pisos/floors, " +
            "techos/roofs, columnas/columns, vigas/beams, escaleras/stairs, mobiliario/furniture, tuberias/pipes, " +
            "ductos/ducts; o cualquier nombre de categoría de Revit (ver get_categories).";

        public override JObject InputSchema => Schema.Object(new[] { "categoria" },
            ("categoria", "string", "Alias o nombre de la categoría, p.ej. \"muros\""),
            ("limite", "integer", "Máximo de elementos a devolver (100 por defecto)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var categoria = args.Str("categoria", args.Str("category", ""));
            if (string.IsNullOrWhiteSpace(categoria)) throw new ToolException("Falta el parámetro obligatorio 'categoria'");
            int limite = args.Limit();

            var elements = ToolHelpers.Instances(doc, categoria).ToList();
            return new
            {
                categoria,
                total = elements.Count,
                devueltos = System.Math.Min(elements.Count, limite),
                elementos = elements.Take(limite).Select(e => ToolHelpers.Brief(doc, e))
            };
        }
    }
}
