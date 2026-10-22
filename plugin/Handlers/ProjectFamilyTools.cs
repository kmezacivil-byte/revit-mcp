#nullable disable
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMcpPlugin.Families;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMcpPlugin.Handlers
{
    public class DuplicateTypeTool : McpTool
    {
        public override string Name => "duplicate_type";
        public override string Description =>
            "Duplica un tipo de una familia del proyecto (familias cargables o de sistema, como muros) con un nombre nuevo. " +
            "El tipo nuevo hereda todos los valores del original; cámbialos con set_parameter en_tipo=true.";
        public override bool ReadOnly => false;
        public override string Example => "{\"familia\":\"Pescante giratorio\",\"tipo_origen\":\"Std\",\"tipo_nuevo\":\"Reforzado\"}";

        public override JObject InputSchema => Schema.Object(new[] { "familia", "tipo_origen", "tipo_nuevo" },
            ("familia", "string", "Nombre de la familia"),
            ("tipo_origen", "string", "Nombre del tipo a duplicar"),
            ("tipo_nuevo", "string", "Nombre del tipo nuevo"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var familia = args.RequireStr("familia");
            var origen = args.RequireStr("tipo_origen");
            var nuevo = args.RequireStr("tipo_nuevo");

            var types = new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>()
                .Where(t => string.Equals(t.FamilyName, familia, StringComparison.OrdinalIgnoreCase)).ToList();
            if (types.Count == 0) throw new ToolException($"No hay tipos de una familia llamada '{familia}' (usa get_families o get_system_types)");
            var source = types.FirstOrDefault(t => ToolHelpers.SafeName(t).Equals(origen, StringComparison.OrdinalIgnoreCase))
                ?? throw new ToolException($"No existe el tipo '{origen}' en '{familia}'. Tipos: {string.Join(", ", types.Select(t => ToolHelpers.SafeName(t)))}");
            if (types.Any(t => ToolHelpers.SafeName(t).Equals(nuevo, StringComparison.OrdinalIgnoreCase)))
                throw new ToolException($"Ya existe un tipo '{nuevo}' en '{familia}'");

            return ToolHelpers.InTransaction(doc, "Duplicar tipo", () =>
            {
                var copy = source.Duplicate(nuevo);
                return new { id = copy.Id.GetId(), familia = copy.FamilyName, tipo = ToolHelpers.SafeName(copy), copiado_de = origen };
            });
        }
    }

    public class GetFamilyGeometryTool : McpTool
    {
        public override string Name => "get_family_geometry";
        public override string Description =>
            "Devuelve la caja envolvente y los sólidos (volumen, área, dimensiones) de un elemento del proyecto (id) o de una familia " +
            "abierta (familia_doc_id), para verificar dimensiones sin abrir Revit. Con familia_doc_id usa el tipo activo: cambia de tipo " +
            "con activate_family_type y vuelve a llamar para comparar. Medidas en metros.";
        public override string Example => "{\"familia_doc_id\":1}";

        public override JObject InputSchema => Schema.Object(
            ("id", "integer", "Id de un elemento del proyecto (instancia o tipo)"),
            ("familia_doc_id", "integer", "Id de una familia abierta (alternativa a 'id')"));

        private sealed class Box
        {
            public double[] Min = { double.MaxValue, double.MaxValue, double.MaxValue };
            public double[] Max = { double.MinValue, double.MinValue, double.MinValue };
            public bool Empty => Min[0] == double.MaxValue;
            public void Add(XYZ p) { var c = new[] { p.X, p.Y, p.Z }; for (int i = 0; i < 3; i++) { Min[i] = Math.Min(Min[i], c[i]); Max[i] = Math.Max(Max[i], c[i]); } }
            public void Add(Box b) { if (b.Empty) return; for (int i = 0; i < 3; i++) { Min[i] = Math.Min(Min[i], b.Min[i]); Max[i] = Math.Max(Max[i], b.Max[i]); } }
            public object ToJson() => Empty ? null : (object)new
            {
                min = new { x = Units.M(Min[0]), y = Units.M(Min[1]), z = Units.M(Min[2]) },
                max = new { x = Units.M(Max[0]), y = Units.M(Max[1]), z = Units.M(Max[2]) },
                dimensiones_m = new { x = Units.M(Max[0] - Min[0]), y = Units.M(Max[1] - Min[1]), z = Units.M(Max[2] - Min[2]) }
            };
        }

        private static IEnumerable<Solid> Walk(GeometryObject o)
        {
            if (o is Solid s && s.Volume > 1e-9) yield return s;
            else if (o is GeometryInstance gi)
                foreach (var child in gi.GetInstanceGeometry())
                    foreach (var r in Walk(child)) yield return r;
        }

        private static List<Solid> Solids(Element e)
        {
            var geometry = e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
            var list = new List<Solid>();
            if (geometry != null) foreach (var o in geometry) list.AddRange(Walk(o));
            return list;
        }

        private static object Summarize(Element e, out Box box)
        {
            box = new Box();
            var solids = Solids(e);
            var rows = new List<object>();
            foreach (var solid in solids)
            {
                var b = new Box();
                foreach (Edge edge in solid.Edges) foreach (var p in edge.Tessellate()) b.Add(p);
                box.Add(b);
                rows.Add(new { volumen_m3 = Units.M3(solid.Volume), area_m2 = Units.M2(solid.SurfaceArea), caras = solid.Faces.Size, caja = b.ToJson() });
            }
            return new { solidos = rows, volumen_total_m3 = Units.M3(solids.Sum(x => x.Volume)) };
        }

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            if (args.Has("id") == args.Has("familia_doc_id"))
                throw new ToolException("Indica 'id' (elemento del proyecto) o 'familia_doc_id' (familia abierta), pero no ambos");

            if (args.Has("id"))
            {
                var doc = ctx.RequireDoc();
                var e = ToolHelpers.RequireElement(doc, args.RequireLong("id"));
                var summary = Summarize(e, out var box);
                return new { id = e.Id.GetId(), nombre = ToolHelpers.SafeName(e), caja_envolvente_m = box.ToJson(), detalle = summary };
            }

            var s = FamilySessions.Require(args);
            var d = s.Doc;
            var pieces = new List<object>();
            var all = new Box();
            var seen = new HashSet<long>();

            void Add(Element e, string name, string kind, bool solid)
            {
                if (!seen.Add(e.Id.GetId())) return;
                if (!solid) { pieces.Add(new { pieza = name, tipo = kind, es_vacio = true }); return; }
                var summary = Summarize(e, out var box);
                all.Add(box);
                pieces.Add(new { pieza = name, tipo = kind, es_vacio = false, caja = box.ToJson(), detalle = summary });
            }

            foreach (var kv in s.Forms)
            {
                var e = d.GetElement(kv.Value.Id);
                if (e == null) continue;
                Add(e, kv.Key, kv.Value.Kind, (e as GenericForm)?.IsSolid ?? true);
            }
            foreach (var t in new[] { typeof(Extrusion), typeof(Sweep), typeof(Revolution) })
                foreach (var e in new FilteredElementCollector(d).OfClass(t))
                    Add(e, ToolHelpers.SafeName(e), t.Name.ToLowerInvariant(), (e as GenericForm)?.IsSolid ?? true);

            var fm = d.FamilyManager;
            return new
            {
                familia_doc_id = s.Id,
                tipo_activo = fm.CurrentType?.Name,
                caja_envolvente_m = all.ToJson(),
                piezas = pieces
            };
        }
    }
}
