#nullable disable
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace RevitMcpPlugin.Handlers
{
    public class DetectClashesTool : McpTool
    {
        private const int MaxFirstSet = 1000;
        private const int BudgetSeconds = 40;   // por debajo del tiempo máximo de 60 s del despachador

        public override string Name => "detect_clashes";
        public override string Description =>
            "Detecta interferencias geométricas entre dos categorías (p.ej. ductos contra vigas). " +
            "Recorre hasta 1000 elementos de la primera categoría y se detiene a los 40 s, indicándolo en 'incompleto'. " +
            "Si ambas categorías son la misma, busca choques entre sus propios elementos.";

        public override JObject InputSchema => Schema.Object(new[] { "categoria_a", "categoria_b" },
            ("categoria_a", "string", "Alias o nombre de la primera categoría"),
            ("categoria_b", "string", "Alias o nombre de la segunda categoría"),
            ("tolerancia_m", "number", "Margen en metros para considerar cercanía (0 por defecto)"),
            ("limite", "integer", "Máximo de interferencias a devolver (100 por defecto)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var filterB = Categories.Filter(doc, args.RequireStr("categoria_b"));
            var setA = ToolHelpers.Instances(doc, args.RequireStr("categoria_a")).ToList();
            bool same = args.Str("categoria_a").Equals(args.Str("categoria_b"), StringComparison.OrdinalIgnoreCase);
            double tol = args.Dbl("tolerancia_m", 0) / Units.FeetToMeters;
            int limite = args.Limit();

            var clashes = new List<object>();
            var seen = new HashSet<string>();
            var clock = Stopwatch.StartNew();
            int analyzed = 0;
            bool incomplete = setA.Count > MaxFirstSet;

            foreach (var a in setA.Take(MaxFirstSet))
            {
                if (clock.Elapsed.TotalSeconds > BudgetSeconds) { incomplete = true; break; }
                analyzed++;

                var bb = a.get_BoundingBox(null);
                if (bb == null) continue;
                var outline = new Outline(bb.Min - new XYZ(tol, tol, tol), bb.Max + new XYZ(tol, tol, tol));

                ICollection<ElementId> hits;
                try
                {
                    hits = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                        .WherePasses(filterB)
                        .WherePasses(new BoundingBoxIntersectsFilter(outline))
                        .WherePasses(new ElementIntersectsElementFilter(a))
                        .ToElementIds();
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { continue; }   // sin geometría sólida

                foreach (var hitId in hits)
                {
                    if (hitId == a.Id) continue;
                    var key = same
                        ? Math.Min(a.Id.GetId(), hitId.GetId()) + "-" + Math.Max(a.Id.GetId(), hitId.GetId())
                        : a.Id.GetId() + "-" + hitId.GetId();
                    if (!seen.Add(key)) continue;
                    if (clashes.Count < limite)
                        clashes.Add(new { a = ToolHelpers.Brief(doc, a), b = ToolHelpers.Brief(doc, doc.GetElement(hitId)) });
                }
            }

            return new
            {
                elementos_analizados = analyzed,
                total_interferencias = seen.Count,
                devueltas = clashes.Count,
                incompleto = incomplete,
                interferencias = clashes
            };
        }
    }

    public class GetMepTypesTool : McpTool
    {
        public override string Name => "get_mep_types";
        public override string Description =>
            "Tipos de ducto, tipos de tubería y tipos de sistema (mecánico y de tuberías) disponibles, " +
            "para usar con create_duct y create_pipe.";

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            List<string> Names<T>() where T : Element =>
                new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>()
                    .Select(t => ToolHelpers.SafeName(t)).OrderBy(n => n).ToList();

            return new
            {
                tipos_ducto = Names<DuctType>(),
                tipos_tuberia = Names<PipeType>(),
                sistemas_mecanicos = Names<MechanicalSystemType>(),
                sistemas_tuberias = Names<PipingSystemType>()
            };
        }
    }

    public class CreateDuctTool : McpTool
    {
        public override string Name => "create_duct";
        public override string Description =>
            "Crea un tramo de ducto entre dos puntos (metros). Usa get_mep_types para ver los tipos disponibles. " +
            "Para ducto circular indica diametro_mm; para rectangular, ancho_mm y alto_mm.";
        public override bool ReadOnly => false;

        public override JObject InputSchema => Schema.Object(new[] { "x1", "y1", "x2", "y2" },
            ("x1", "number", "X inicial (m)"), ("y1", "number", "Y inicial (m)"), ("z1", "number", "Z inicial sobre el nivel (m, 0 por defecto)"),
            ("x2", "number", "X final (m)"), ("y2", "number", "Y final (m)"), ("z2", "number", "Z final sobre el nivel (m, 0 por defecto)"),
            ("nivel", "string", "Nombre del nivel; vacío = el más bajo"),
            ("tipo_ducto", "string", "Nombre del tipo de ducto; vacío = el primero"),
            ("tipo_sistema", "string", "Nombre del tipo de sistema mecánico; vacío = el primero"),
            ("diametro_mm", "number", "Diámetro (ducto circular)"),
            ("ancho_mm", "number", "Ancho (ducto rectangular)"), ("alto_mm", "number", "Alto (ducto rectangular)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var level = ToolHelpers.FindLevel(doc, args.Str("nivel"));
            var ductType = ToolHelpers.FindType<DuctType>(doc, args.Str("tipo_ducto"), "tipos de ducto");
            var systemType = ToolHelpers.FindType<MechanicalSystemType>(doc, args.Str("tipo_sistema"), "tipos de sistema mecánico");
            var p1 = new XYZ(args.RequireDbl("x1") / Units.FeetToMeters, args.RequireDbl("y1") / Units.FeetToMeters,
                level.Elevation + args.Dbl("z1", 0) / Units.FeetToMeters);
            var p2 = new XYZ(args.RequireDbl("x2") / Units.FeetToMeters, args.RequireDbl("y2") / Units.FeetToMeters,
                level.Elevation + args.Dbl("z2", 0) / Units.FeetToMeters);
            if (p1.DistanceTo(p2) < 1e-6) throw new ToolException("Los puntos coinciden: el ducto no tendría longitud");

            return ToolHelpers.InTransaction(doc, "Crear ducto", () =>
            {
                var duct = Duct.Create(doc, systemType.Id, ductType.Id, level.Id, p1, p2);
                SetMm(duct, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, args, "diametro_mm");
                SetMm(duct, BuiltInParameter.RBS_CURVE_WIDTH_PARAM, args, "ancho_mm");
                SetMm(duct, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, args, "alto_mm");
                return new { id = duct.Id.GetId(), tipo = ductType.Name, sistema = systemType.Name, longitud_m = Units.M(p1.DistanceTo(p2)) };
            });
        }

        internal static void SetMm(Element e, BuiltInParameter bip, ToolArgs args, string key)
        {
            if (!args.Has(key)) return;
            var p = e.get_Parameter(bip);
            if (p == null || p.IsReadOnly) throw new ToolException($"No se puede aplicar '{key}' a este elemento");
            p.Set(args.Dbl(key, 0) / 304.8);
        }
    }

    public class CreatePipeTool : McpTool
    {
        public override string Name => "create_pipe";
        public override string Description =>
            "Crea un tramo de tubería entre dos puntos (metros). Usa get_mep_types para ver los tipos disponibles.";
        public override bool ReadOnly => false;

        public override JObject InputSchema => Schema.Object(new[] { "x1", "y1", "x2", "y2" },
            ("x1", "number", "X inicial (m)"), ("y1", "number", "Y inicial (m)"), ("z1", "number", "Z inicial sobre el nivel (m, 0 por defecto)"),
            ("x2", "number", "X final (m)"), ("y2", "number", "Y final (m)"), ("z2", "number", "Z final sobre el nivel (m, 0 por defecto)"),
            ("nivel", "string", "Nombre del nivel; vacío = el más bajo"),
            ("tipo_tuberia", "string", "Nombre del tipo de tubería; vacío = el primero"),
            ("tipo_sistema", "string", "Nombre del tipo de sistema de tuberías; vacío = el primero"),
            ("diametro_mm", "number", "Diámetro en mm"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var level = ToolHelpers.FindLevel(doc, args.Str("nivel"));
            var pipeType = ToolHelpers.FindType<PipeType>(doc, args.Str("tipo_tuberia"), "tipos de tubería");
            var systemType = ToolHelpers.FindType<PipingSystemType>(doc, args.Str("tipo_sistema"), "tipos de sistema de tuberías");
            var p1 = new XYZ(args.RequireDbl("x1") / Units.FeetToMeters, args.RequireDbl("y1") / Units.FeetToMeters,
                level.Elevation + args.Dbl("z1", 0) / Units.FeetToMeters);
            var p2 = new XYZ(args.RequireDbl("x2") / Units.FeetToMeters, args.RequireDbl("y2") / Units.FeetToMeters,
                level.Elevation + args.Dbl("z2", 0) / Units.FeetToMeters);
            if (p1.DistanceTo(p2) < 1e-6) throw new ToolException("Los puntos coinciden: la tubería no tendría longitud");

            return ToolHelpers.InTransaction(doc, "Crear tubería", () =>
            {
                var pipe = Pipe.Create(doc, systemType.Id, pipeType.Id, level.Id, p1, p2);
                CreateDuctTool.SetMm(pipe, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, args, "diametro_mm");
                return new { id = pipe.Id.GetId(), tipo = pipeType.Name, sistema = systemType.Name, longitud_m = Units.M(p1.DistanceTo(p2)) };
            });
        }
    }
}
