using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Linq;

namespace RevitMcpPlugin.Handlers
{
    public class GetElementsByCategoryHandler : ICommandHandler
    {
        private static readonly Dictionary<string, BuiltInCategory> CategoryMap =
            new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase)
        {
            { "muros",      BuiltInCategory.OST_Walls },
            { "walls",      BuiltInCategory.OST_Walls },
            { "puertas",    BuiltInCategory.OST_Doors },
            { "doors",      BuiltInCategory.OST_Doors },
            { "ventanas",   BuiltInCategory.OST_Windows },
            { "windows",    BuiltInCategory.OST_Windows },
            { "pisos",      BuiltInCategory.OST_Floors },
            { "floors",     BuiltInCategory.OST_Floors },
            { "techos",     BuiltInCategory.OST_Roofs },
            { "roofs",      BuiltInCategory.OST_Roofs },
            { "columnas",   BuiltInCategory.OST_Columns },
            { "columns",    BuiltInCategory.OST_Columns },
            { "vigas",      BuiltInCategory.OST_StructuralFraming },
            { "beams",      BuiltInCategory.OST_StructuralFraming },
            { "escaleras",  BuiltInCategory.OST_Stairs },
            { "stairs",     BuiltInCategory.OST_Stairs },
            { "mobiliario", BuiltInCategory.OST_Furniture },
            { "furniture",  BuiltInCategory.OST_Furniture },
            { "tuberias",   BuiltInCategory.OST_PipeCurves },
            { "pipes",      BuiltInCategory.OST_PipeCurves },
            { "ductos",     BuiltInCategory.OST_DuctCurves },
            { "ducts",      BuiltInCategory.OST_DuctCurves },
        };

        public string Execute(Dictionary<string, string> args, Document doc, UIApplication uiApp)
        {
            if (doc == null)
                return JsonConvert.SerializeObject(new { error = "No hay documento activo en Revit" });

            var categoryName = args.GetValueOrDefault("categoria", args.GetValueOrDefault("category", ""));
            if (string.IsNullOrWhiteSpace(categoryName))
                return JsonConvert.SerializeObject(new
                {
                    error = "Se requiere el parametro 'categoria'",
                    categorias_disponibles = CategoryMap.Keys.ToArray()
                });

            if (!CategoryMap.TryGetValue(categoryName, out var bic))
                return JsonConvert.SerializeObject(new
                {
                    error = $"Categoria '{categoryName}' no reconocida",
                    categorias_disponibles = CategoryMap.Keys.ToArray()
                });

            var elements = new FilteredElementCollector(doc)
                .OfCategory(bic)
                .WhereElementIsNotElementType()
                .Select(e => new
                {
                    id = e.Id.GetId(),
                    nombre = e.Name,
                    tipo = doc.GetElement(e.GetTypeId())?.Name ?? "",
                    nivel = (e.LevelId != null && e.LevelId != ElementId.InvalidElementId)
                        ? (doc.GetElement(e.LevelId) as Level)?.Name ?? ""
                        : ""
                })
                .ToList();

            return JsonConvert.SerializeObject(new
            {
                categoria = categoryName,
                total = elements.Count,
                elementos = elements
            });
        }
    }
}
