#nullable disable
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;

namespace RevitMcpPlugin.Handlers
{
    public class CreateWallTool : McpTool
    {
        public override string Name => "create_wall";
        public override string Description =>
            "Crea un muro recto en el proyecto activo (coordenadas en metros). Se puede deshacer con Ctrl+Z.";
        public override bool ReadOnly => false;

        public override JObject InputSchema => Schema.Object(new[] { "x1", "y1", "x2", "y2" },
            ("x1", "number", "X inicial (m)"), ("y1", "number", "Y inicial (m)"),
            ("x2", "number", "X final (m)"), ("y2", "number", "Y final (m)"),
            ("nivel", "string", "Nombre del nivel; vacío = el más bajo"),
            ("tipo_muro", "string", "Nombre del tipo de muro; vacío = el primero"),
            ("altura_m", "number", "Altura en metros (3.0 por defecto)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();

            double x1 = args.RequireDbl("x1") / Units.FeetToMeters;
            double y1 = args.RequireDbl("y1") / Units.FeetToMeters;
            double x2 = args.RequireDbl("x2") / Units.FeetToMeters;
            double y2 = args.RequireDbl("y2") / Units.FeetToMeters;
            double alturaM = args.Dbl("altura_m", 3.0);
            var levelName = args.Str("nivel", args.Str("level", ""));
            var wallTypeName = args.Str("tipo_muro", args.Str("wall_type", ""));

            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            Level level = string.IsNullOrWhiteSpace(levelName)
                ? levels.OrderBy(l => l.Elevation).FirstOrDefault()
                : levels.FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
            if (level == null) throw new ToolException($"Nivel '{levelName}' no encontrado");

            var wallTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().ToList();
            WallType wallType = string.IsNullOrWhiteSpace(wallTypeName)
                ? wallTypes.FirstOrDefault()
                : wallTypes.FirstOrDefault(wt => wt.Name.Equals(wallTypeName, StringComparison.OrdinalIgnoreCase));
            if (wallType == null) throw new ToolException($"Tipo de muro '{wallTypeName}' no encontrado");

            if (Math.Abs(x1 - x2) < 1e-9 && Math.Abs(y1 - y2) < 1e-9)
                throw new ToolException("El punto inicial y final coinciden: el muro no tendría longitud");

            using (var tx = new Transaction(doc, "Crear Muro (MCP)"))
            {
                tx.Start();
                var line = Line.CreateBound(new XYZ(x1, y1, level.Elevation), new XYZ(x2, y2, level.Elevation));
                var wall = Wall.Create(doc, line, wallType.Id, level.Id, alturaM / Units.FeetToMeters, 0, false, false);
                tx.Commit();

                return new
                {
                    id = wall.Id.GetId(),
                    nombre = wall.Name,
                    tipo = wallType.Name,
                    nivel = level.Name,
                    longitud_m = Math.Round(line.Length * Units.FeetToMeters, 3),
                    altura_m = alturaM
                };
            }
        }
    }
}
