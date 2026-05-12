using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Linq;

namespace RevitMcpPlugin.Handlers
{
    public class CreateWallHandler : ICommandHandler
    {
        public string Execute(Dictionary<string, string> args, Document doc, UIApplication uiApp)
        {
            if (doc == null)
                return JsonConvert.SerializeObject(new { error = "No hay documento activo en Revit" });

            // Coordenadas en metros -> convertir a pies (unidad interna de Revit)
            double x1 = Parse(args, "x1", 0) / 0.3048;
            double y1 = Parse(args, "y1", 0) / 0.3048;
            double x2 = Parse(args, "x2", 5) / 0.3048;
            double y2 = Parse(args, "y2", 0) / 0.3048;

            var levelName = args.GetValueOrDefault("nivel", args.GetValueOrDefault("level", ""));
            var wallTypeName = args.GetValueOrDefault("tipo_muro", args.GetValueOrDefault("wall_type", ""));
            double alturaM = Parse(args, "altura_m", 3.0);

            // Buscar nivel
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .ToList();

            Level level = string.IsNullOrWhiteSpace(levelName)
                ? levels.OrderBy(l => l.Elevation).First()
                : levels.FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));

            if (level == null)
                return JsonConvert.SerializeObject(new { error = $"Nivel '{levelName}' no encontrado" });

            // Buscar tipo de muro (opcional)
            WallType wallType = null;
            if (!string.IsNullOrWhiteSpace(wallTypeName))
            {
                wallType = new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .FirstOrDefault(wt => wt.Name.Equals(wallTypeName, StringComparison.OrdinalIgnoreCase));
            }
            wallType ??= new FilteredElementCollector(doc)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .First();

            using var tx = new Transaction(doc, "Crear Muro (MCP)");
            tx.Start();

            var line = Line.CreateBound(new XYZ(x1, y1, level.Elevation), new XYZ(x2, y2, level.Elevation));
            var wall = Wall.Create(doc, line, wallType.Id, level.Id, alturaM / 0.3048, 0, false, false);

            tx.Commit();

            return JsonConvert.SerializeObject(new
            {
                id = wall.Id.GetId(),
                nombre = wall.Name,
                tipo = wallType.Name,
                nivel = level.Name,
                longitud_m = Math.Round(line.Length * 0.3048, 3),
                altura_m = alturaM
            });
        }

        private static double Parse(Dictionary<string, string> args, string key, double defaultVal)
        {
            if (args.TryGetValue(key, out var s) && double.TryParse(s,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
                return v;
            return defaultVal;
        }
    }
}
