#nullable disable
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;
using System.Linq;

namespace RevitMcpPlugin.Handlers
{
    public class GetRoomsTool : McpTool
    {
        public override string Name => "get_rooms";
        public override string Description =>
            "Lista los ambientes con área > 0: número, nombre, área (m²), nivel y perímetro (m). " +
            "Filtros opcionales: nivel (nombre exacto) y limite.";

        public override JObject InputSchema => Schema.Object(
            ("nivel", "string", "Nombre del nivel para filtrar"),
            ("limite", "integer", "Máximo de ambientes a devolver (100 por defecto)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var nivel = args.Str("nivel");
            int limite = args.Limit();

            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .Cast<Room>()
                .Where(r => r.Area > 0)
                .Where(r => nivel == "" || string.Equals(r.Level?.Name, nivel, System.StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Number)
                .ToList();

            return new
            {
                total = rooms.Count,
                devueltos = System.Math.Min(rooms.Count, limite),
                ambientes = rooms.Take(limite).Select(r => new
                {
                    id = r.Id.GetId(),
                    numero = r.Number,
                    nombre = ToolHelpers.SafeName(r),
                    area_m2 = Units.M2(r.Area),
                    nivel = r.Level?.Name ?? "",
                    perimetro_m = Units.M(r.Perimeter)
                })
            };
        }
    }
}
