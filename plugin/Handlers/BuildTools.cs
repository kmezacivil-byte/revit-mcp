#nullable disable
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMcpPlugin.Handlers
{
    public class GetSystemTypesTool : McpTool
    {
        public override string Name => "get_system_types";
        public override string Description =>
            "Tipos de familias de sistema que get_families no devuelve: muros, pisos, techos o cielorrasos. " +
            "Úsala para saber el nombre exacto antes de crear o cambiar tipos.";

        public override JObject InputSchema => Schema.Object(new[] { "clase" },
            ("clase", "string", "\"muro\", \"piso\", \"techo\" o \"cielorraso\""));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var clase = args.RequireStr("clase").ToLowerInvariant();
            Type type;
            switch (clase)
            {
                case "muro": case "wall": type = typeof(WallType); break;
                case "piso": case "floor": type = typeof(FloorType); break;
                case "techo": case "roof": type = typeof(RoofType); break;
                case "cielorraso": case "ceiling": type = typeof(CeilingType); break;
                default: throw new ToolException("'clase' debe ser \"muro\", \"piso\", \"techo\" o \"cielorraso\"");
            }

            var types = new FilteredElementCollector(doc).OfClass(type).Cast<ElementType>()
                .OrderBy(t => ToolHelpers.SafeName(t)).ToList();
            return new
            {
                clase,
                total = types.Count,
                tipos = types.Select(t => new
                {
                    id = t.Id.GetId(),
                    nombre = ToolHelpers.SafeName(t),
                    familia = t.FamilyName,
                    clase_muro = (t as WallType)?.Kind.ToString(),
                    ancho_m = t is WallType w ? (double?)Units.M(w.Width) : null
                })
            };
        }
    }

    public class SelectElementsTool : McpTool
    {
        public override string Name => "select_elements";
        public override string Description =>
            "Selecciona elementos en Revit por id, para que el usuario los vea resaltados. Reemplaza la selección actual.";
        public override bool ReadOnly => false;

        public override JObject InputSchema => Schema.Object(new[] { "ids" },
            ("ids", "integer[]", "Ids de los elementos (máximo 500)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var ids = ModifyHelpers.RequireElements(doc, args);
            ctx.App.ActiveUIDocument.Selection.SetElementIds(ids);
            return new { seleccionados = ids.Count };
        }
    }

    public class ChangeElementTypeTool : McpTool
    {
        public override string Name => "change_element_type";
        public override string Description =>
            "Cambia el tipo de uno o varios elementos existentes sin borrarlos ni recrearlos (p.ej. otro tipo de muro). " +
            "El tipo debe ser compatible con el elemento. Revit puede asignar nuevos ids: se devuelven.";
        public override bool ReadOnly => false;
        public override bool Destructive => true;

        public override JObject InputSchema => Schema.Object(new[] { "ids", "tipo" },
            ("ids", "integer[]", "Ids de los elementos (máximo 500)"),
            ("tipo", "string", "Nombre exacto del nuevo tipo"),
            ("familia", "string", "Nombre de la familia, si varias familias tienen un tipo con ese nombre"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var ids = ModifyHelpers.RequireElements(doc, args);
            var tipo = args.RequireStr("tipo");
            var familia = args.Str("familia");

            var first = doc.GetElement(ids[0]);
            var candidates = first.GetValidTypes()
                .Select(id => doc.GetElement(id) as ElementType).Where(t => t != null)
                .Where(t => ToolHelpers.SafeName(t).Equals(tipo, StringComparison.OrdinalIgnoreCase))
                .Where(t => familia == "" || (t.FamilyName ?? "").Equals(familia, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 0)
                throw new ToolException($"No hay un tipo '{tipo}' compatible con ese elemento. Algunos válidos: " +
                    string.Join(", ", first.GetValidTypes().Select(id => ToolHelpers.SafeName(doc.GetElement(id))).Take(15)));
            if (candidates.Count > 1)
                throw new ToolException($"Hay {candidates.Count} tipos llamados '{tipo}' en familias distintas ({string.Join(", ", candidates.Select(t => t.FamilyName))}). Indica 'familia'");

            var target = candidates[0];
            return ToolHelpers.InTransaction(doc, "Cambiar tipo", () =>
            {
                var result = new List<object>();
                foreach (var id in ids)
                {
                    var e = doc.GetElement(id);
                    if (!e.GetValidTypes().Contains(target.Id))
                        throw new ToolException($"El tipo '{tipo}' no es compatible con el elemento {id.GetId()}");
                    var newId = e.ChangeTypeId(target.Id);
                    result.Add(new { id_anterior = id.GetId(), id_nuevo = newId.GetId() });
                }
                return new { cambiados = ids.Count, tipo = ToolHelpers.SafeName(target), elementos = result };
            });
        }
    }

    public class CreateRoofTool : McpTool
    {
        public override string Name => "create_roof";
        public override string Description =>
            "Crea un techo por contorno a partir de un polígono cerrado [[x,y],...] en metros. Con pendiente_grados " +
            "inclina los lados indicados (por defecto todos, formando un techo a cuatro aguas); lados_inclinados usa " +
            "índices desde 0 en el orden de los puntos (lado i va del punto i al i+1). Sin pendiente queda plano.";
        public override bool ReadOnly => false;

        public override JObject InputSchema => Schema.Object(new[] { "puntos" },
            ("puntos", "number[][]", "Vértices del contorno en orden: [[x,y],...] (m), mínimo 3"),
            ("nivel", "string", "Nivel de base del techo; vacío = el más alto"),
            ("tipo_techo", "string", "Nombre del tipo de techo (ver get_system_types); vacío = el primero"),
            ("pendiente_grados", "number", "Ángulo de inclinación en grados (opcional)"),
            ("lados_inclinados", "integer[]", "Índices de los lados que llevan pendiente (todos por defecto)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var pts = args.Points("puntos", 3);
            var levelName = args.Str("nivel");
            var level = levelName != "" ? ToolHelpers.FindLevel(doc, levelName)
                : new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderByDescending(l => l.Elevation).FirstOrDefault();
            if (level == null) throw new ToolException("El proyecto no tiene niveles");
            var roofType = ToolHelpers.FindType<RoofType>(doc, args.Str("tipo_techo"), "tipos de techo");

            var curves = new CurveArray();
            for (int i = 0; i < pts.Count; i++)
            {
                var a = new XYZ(pts[i][0] / Units.FeetToMeters, pts[i][1] / Units.FeetToMeters, level.Elevation);
                var b = new XYZ(pts[(i + 1) % pts.Count][0] / Units.FeetToMeters, pts[(i + 1) % pts.Count][1] / Units.FeetToMeters, level.Elevation);
                if (a.DistanceTo(b) < 1e-6) throw new ToolException("Hay dos vértices consecutivos iguales");
                curves.Append(Line.CreateBound(a, b));
            }

            bool slope = args.Has("pendiente_grados");
            var sides = args.Has("lados_inclinados") ? args.LongList("lados_inclinados").Select(l => (int)l).ToList()
                : Enumerable.Range(0, pts.Count).ToList();
            if (sides.Any(s => s < 0 || s >= pts.Count)) throw new ToolException($"'lados_inclinados' debe estar entre 0 y {pts.Count - 1}");
            var tan = Math.Tan(args.Dbl("pendiente_grados", 0) * Math.PI / 180.0);

            return ToolHelpers.InTransaction(doc, "Crear techo", () =>
            {
                ModelCurveArray mapping = new ModelCurveArray();
                var roof = doc.Create.NewFootPrintRoof(curves, level, roofType, out mapping);
                if (slope)
                    foreach (var i in sides)
                    {
                        var mc = mapping.get_Item(i);
                        roof.set_DefinesSlope(mc, true);
                        roof.set_SlopeAngle(mc, tan);
                    }
                return new
                {
                    id = roof.Id.GetId(),
                    tipo = roofType.Name,
                    nivel = level.Name,
                    lados_inclinados = slope ? sides.Count : 0,
                    pendiente_grados = slope ? args.Dbl("pendiente_grados", 0) : 0
                };
            });
        }
    }

    public abstract class CreateHostedTool : McpTool
    {
        protected abstract BuiltInCategory Category { get; }
        protected abstract string Label { get; }     // "puerta" / "ventana"
        protected virtual bool HasSill => false;

        public override bool ReadOnly => false;

        public override JObject InputSchema
        {
            get
            {
                var props = new List<(string, string, string)>
                {
                    ("muro_id", "integer", "Id del muro anfitrión (ver get_elements_by_category)"),
                    ("familia", "string", $"Nombre de la familia de {Label} cargada en el proyecto"),
                    ("tipo", "string", "Nombre del tipo; vacío = el primero de la familia"),
                    ("distancia_m", "number", "Distancia en metros desde el inicio del muro hasta el centro de la " + Label),
                };
                if (HasSill) props.Add(("altura_antepecho_m", "number", "Altura del antepecho sobre el nivel en metros (0.9 por defecto)"));
                return Schema.Object(new[] { "muro_id", "familia", "distancia_m" }, props.ToArray());
            }
        }

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var wall = ToolHelpers.RequireElement(doc, args.RequireLong("muro_id")) as Wall;
            if (wall == null) throw new ToolException("El id indicado no es un muro");
            var curve = (wall.Location as LocationCurve)?.Curve;
            if (curve == null) throw new ToolException("Ese muro no tiene una línea de ubicación");

            var distance = args.RequireDbl("distancia_m") / Units.FeetToMeters;
            if (distance < 0 || distance > curve.Length)
                throw new ToolException($"'distancia_m' debe estar entre 0 y {Units.M(curve.Length)} (largo del muro)");

            var familia = args.RequireStr("familia");
            var tipo = args.Str("tipo");
            var symbols = new FilteredElementCollector(doc).OfCategory(Category).WhereElementIsElementType()
                .Cast<FamilySymbol>()
                .Where(s => string.Equals(s.FamilyName, familia, StringComparison.OrdinalIgnoreCase)).ToList();
            if (symbols.Count == 0)
                throw new ToolException($"No hay una familia de {Label} llamada '{familia}' cargada. " +
                    $"Usa get_families (categoria \"{Label}s\") o cárgala con load_family");
            var symbol = tipo == "" ? symbols[0]
                : symbols.FirstOrDefault(s => ToolHelpers.SafeName(s).Equals(tipo, StringComparison.OrdinalIgnoreCase));
            if (symbol == null)
                throw new ToolException($"Tipo '{tipo}' no existe en '{familia}'. Tipos: " +
                    string.Join(", ", symbols.Select(s => ToolHelpers.SafeName(s))));

            var level = doc.GetElement(wall.LevelId) as Level ?? ToolHelpers.FindLevel(doc, "");
            var sill = HasSill ? args.Dbl("altura_antepecho_m", 0.9) / Units.FeetToMeters : 0;
            var onCurve = curve.Evaluate(distance / curve.Length, true);
            var point = new XYZ(onCurve.X, onCurve.Y, level.Elevation + sill);

            return ToolHelpers.InTransaction(doc, "Colocar " + Label, () =>
            {
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                FamilyInstance inst;
                try { inst = doc.Create.NewFamilyInstance(point, symbol, wall, level, StructuralType.NonStructural); }
                catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                {
                    throw new ToolException($"No se pudo colocar la {Label} en ese muro: {ex.Message}");
                }
                if (HasSill)
                    inst.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.Set(sill);
                return new
                {
                    id = inst.Id.GetId(),
                    familia = symbol.FamilyName,
                    tipo = ToolHelpers.SafeName(symbol),
                    muro_id = wall.Id.GetId(),
                    distancia_m = Units.M(distance)
                };
            });
        }
    }

    public class CreateDoorTool : CreateHostedTool
    {
        public override string Name => "create_door";
        public override string Description =>
            "Coloca una puerta alojada en un muro, a una distancia dada desde el inicio del muro. " +
            "Si la familia no está cargada, cárgala antes con load_family.";
        protected override BuiltInCategory Category => BuiltInCategory.OST_Doors;
        protected override string Label => "puerta";
    }

    public class CreateWindowTool : CreateHostedTool
    {
        public override string Name => "create_window";
        public override string Description =>
            "Coloca una ventana alojada en un muro, a una distancia dada desde el inicio del muro y con una altura de antepecho. " +
            "Si la familia no está cargada, cárgala antes con load_family.";
        protected override BuiltInCategory Category => BuiltInCategory.OST_Windows;
        protected override string Label => "ventana";
        protected override bool HasSill => true;
    }
}
