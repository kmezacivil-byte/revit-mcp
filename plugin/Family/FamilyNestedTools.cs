#nullable disable
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitMcpPlugin.Handlers;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RevitMcpPlugin.Families
{
    internal static class NestedOps
    {
        /// <summary>Carga (si hace falta) la familia a anidar en el documento host y devuelve su Family.</summary>
        public static Autodesk.Revit.DB.Family Resolve(FamilySession host, ToolArgs args)
        {
            var d = host.Doc;
            var opts = new FamilyLoadOptions(true, false);

            if (args.Has("anidada_doc_id"))
            {
                var src = FamilySessions.Require(args, "anidada_doc_id");
                if (src.Id == host.Id) throw new ToolException("Una familia no puede anidarse en sí misma");
                var loaded = src.Doc.LoadFamily(d, opts);
                if (loaded != null) return loaded;
                return FindLoaded(d, src.Name) ?? throw new ToolException($"No se pudo cargar '{src.Name}' en la familia host");
            }

            var familia = args.RequireStr("familia");
            if (familia.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
            {
                if (!Path.IsPathRooted(familia) || !File.Exists(familia)) throw new ToolException($"No existe el archivo: {familia}");
                Autodesk.Revit.DB.Family f;
                if (d.LoadFamily(familia, opts, out f) && f != null) return f;
                return FindLoaded(d, Path.GetFileNameWithoutExtension(familia))
                    ?? throw new ToolException($"No se pudo cargar '{familia}' en la familia host");
            }
            return FindLoaded(d, familia) ?? throw new ToolException(
                $"La familia '{familia}' no está cargada en la familia host. Indica la ruta de un .rfa, o 'anidada_doc_id' de una familia abierta");
        }

        private static Autodesk.Revit.DB.Family FindLoaded(Document d, string name) =>
            new FilteredElementCollector(d).OfClass(typeof(Autodesk.Revit.DB.Family)).Cast<Autodesk.Revit.DB.Family>()
                .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

        public static FamilyInstance Instance(FamilySession s, string name)
        {
            if (!s.Forms.TryGetValue(name, out var form) || form.Kind != "nested")
                throw new ToolException($"No hay una familia anidada llamada '{name}' (usa list_family_elements)");
            return s.Doc.GetElement(form.Id) as FamilyInstance
                ?? throw new ToolException($"La instancia anidada '{name}' ya no existe en la familia");
        }
    }

    public class NestFamilyTool : McpTool
    {
        public override string Name => "nest_family";
        public override string Description =>
            "Anida una familia dentro de una familia abierta (host) y la coloca en un punto con una rotación. La familia a anidar " +
            "se indica con 'familia' (nombre ya cargado en el host, o ruta .rfa) o con 'anidada_doc_id' (otra familia abierta con " +
            "create_family, sin necesidad de guardarla). Devuelve los parámetros de la anidada, para usar con associate_nested_parameter.";
        public override bool ReadOnly => false;
        public override string Example => "{\"familia_doc_id\":2,\"anidada_doc_id\":1,\"nombre\":\"Tubo_fijo\",\"punto\":[0,0,0],\"rotacion_xyz\":[0,0,90]}";

        public override JObject InputSchema => Schema.Object(new[] { "familia_doc_id", "nombre" },
            ("familia_doc_id", "integer", "Familia host (id devuelto por create_family)"),
            ("nombre", "string", "Nombre único de esta instancia anidada, para referirla después"),
            ("familia", "string", "Nombre de una familia cargada en el host, o ruta absoluta de un .rfa"),
            ("anidada_doc_id", "integer", "Alternativa a 'familia': id de otra familia abierta"),
            ("tipo", "string", "Tipo de la familia anidada; vacío = el primero"),
            ("punto", "number[]", "Posición [x, y, z] en metros ([0,0,0] por defecto)"),
            ("rotacion_xyz", "number[]", "Giro en grados alrededor de X, Y y Z, en ese orden ([0,0,0] por defecto)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var s = FamilySessions.Require(args);
            var name = args.RequireStr("nombre");
            if (s.Forms.ContainsKey(name)) throw new ToolException($"Ya existe una pieza llamada '{name}' en esta familia");
            if (args.Has("familia") == args.Has("anidada_doc_id"))
                throw new ToolException("Indica 'familia' (nombre o ruta .rfa) o 'anidada_doc_id', pero no ambos");

            var p = args.Vec3("punto", new double[] { 0, 0, 0 });
            var r = args.Vec3("rotacion_xyz", new double[] { 0, 0, 0 });

            return ToolHelpers.InTransaction(s.Doc, "Anidar familia", () =>
            {
                var family = NestedOps.Resolve(s, args);
                var tipo = args.Str("tipo");
                var symbols = family.GetFamilySymbolIds().Select(id => s.Doc.GetElement(id) as FamilySymbol).Where(x => x != null).ToList();
                if (symbols.Count == 0) throw new ToolException($"La familia '{family.Name}' no tiene tipos");
                var symbol = tipo == "" ? symbols[0]
                    : symbols.FirstOrDefault(x => ToolHelpers.SafeName(x).Equals(tipo, StringComparison.OrdinalIgnoreCase))
                      ?? throw new ToolException($"Tipo '{tipo}' no existe en '{family.Name}'. Tipos: {string.Join(", ", symbols.Select(x => ToolHelpers.SafeName(x)))}");
                if (!symbol.IsActive) { symbol.Activate(); s.Doc.Regenerate(); }

                var point = new XYZ(p[0], p[1], p[2]) / Units.FeetToMeters;
                FamilyInstance inst;
                try { inst = s.Doc.FamilyCreate.NewFamilyInstance(point, symbol, StructuralType.NonStructural); }
                catch (Exception ex)
                {
                    throw new ToolException($"Revit no pudo colocar '{family.Name}' en la familia: {ex.Message}. " +
                        "Si la familia anidada está basada en una cara o en un plano de trabajo, créala como 'modelo_generico' sin anfitrión");
                }

                var axes = new[] { XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ };
                for (int i = 0; i < 3; i++)
                    if (Math.Abs(r[i]) > 1e-9)
                        ElementTransformUtils.RotateElement(s.Doc, inst.Id, Line.CreateBound(point, point + axes[i]), r[i] * Math.PI / 180.0);

                s.Forms[name] = new FormInfo { Id = inst.Id, Kind = "nested" };
                s.Doc.Regenerate();

                return new
                {
                    pieza = name,
                    id = inst.Id.GetId(),
                    familia = family.Name,
                    tipo = ToolHelpers.SafeName(symbol),
                    parametros_instancia = InstanceParams(inst),
                    parametros_tipo = TypeParams(symbol)
                };
            });
        }

        private static IEnumerable<object> InstanceParams(FamilyInstance i) => Describe(i.Parameters.Cast<Parameter>());
        private static IEnumerable<object> TypeParams(FamilySymbol t) => Describe(t.Parameters.Cast<Parameter>());

        private static IEnumerable<object> Describe(IEnumerable<Parameter> ps) =>
            ps.Where(x => x.Definition != null && !x.IsReadOnly && x.StorageType != StorageType.None)
              .Select(x => new { nombre = x.Definition.Name, tipo_dato = FamilyValues.KindName(FamilyValues.KindOf(x.Definition)) })
              .OrderBy(x => x.nombre).ToList();
    }

    public class AssociateNestedParameterTool : McpTool
    {
        public override string Name => "associate_nested_parameter";
        public override string Description =>
            "Vincula un parámetro de una familia anidada (de instancia o de tipo) a un parámetro de la familia host: al cambiar el " +
            "del host, la anidada lo sigue. Los tipos de dato deben coincidir (Longitud con Longitud...). Con crear_parametro_host=true " +
            "crea el parámetro del host con el mismo tipo de dato si no existe.";
        public override bool ReadOnly => false;
        public override string Example => "{\"familia_doc_id\":2,\"instancia\":\"Tubo_fijo\",\"parametro_anidado\":\"Longitud\",\"parametro_host\":\"Altura_Total\"}";

        public override JObject InputSchema => Schema.Object(new[] { "familia_doc_id", "instancia", "parametro_anidado", "parametro_host" },
            ("familia_doc_id", "integer", "Familia host"),
            ("instancia", "string", "Nombre de la instancia anidada (el 'nombre' usado en nest_family)"),
            ("parametro_anidado", "string", "Parámetro de la familia anidada"),
            ("parametro_host", "string", "Parámetro de la familia host"),
            ("crear_parametro_host", "boolean", "Crear el parámetro host si no existe (false por defecto)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var s = FamilySessions.Require(args);
            var inst = NestedOps.Instance(s, args.RequireStr("instancia"));
            var nestedName = args.RequireStr("parametro_anidado");
            var hostName = args.RequireStr("parametro_host");

            return ToolHelpers.InTransaction(s.Doc, "Asociar parámetro anidado", () =>
            {
                var fm = s.Doc.FamilyManager;
                var p = inst.LookupParameter(nestedName) ?? inst.Symbol.LookupParameter(nestedName);
                if (p == null)
                    throw new ToolException($"La familia anidada no tiene un parámetro '{nestedName}'. Disponibles: " +
                        string.Join(", ", inst.Parameters.Cast<Parameter>().Concat(inst.Symbol.Parameters.Cast<Parameter>())
                            .Where(x => x.Definition != null && !x.IsReadOnly).Select(x => x.Definition.Name).Distinct().OrderBy(n => n).Take(30)));
                if (!fm.CanElementParameterBeAssociated(p))
                    throw new ToolException($"El parámetro '{nestedName}' no se puede asociar a un parámetro de la familia host");

                var host = fm.get_Parameter(hostName);
                bool created = false;
                if (host == null)
                {
                    if (!args.Bool("crear_parametro_host", false))
                        throw new ToolException($"La familia host no tiene un parámetro '{hostName}'. Créalo con add_family_parameter o usa crear_parametro_host=true");
                    var kind = FamilyValues.KindOf(p.Definition);
                    if (kind == ParamKind.Other) throw new ToolException($"No se puede crear automáticamente un parámetro para el tipo de '{nestedName}'");
                    host = FamilyOps.AddParameter(s.Doc, hostName, FamilyValues.KindName(kind), "Geometria", false);
                    created = true;
                }

                try { fm.AssociateElementParameterToFamilyParameter(p, host); }
                catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                {
                    throw new ToolException($"No se pudo asociar '{nestedName}' ({FamilyValues.KindName(FamilyValues.KindOf(p.Definition))}) con '{hostName}' " +
                        $"({FamilyValues.KindName(FamilyValues.KindOf(host))}): {ex.Message}. Los tipos de dato deben coincidir");
                }
                s.Doc.Regenerate();
                return new { instancia = args.RequireStr("instancia"), parametro_anidado = nestedName, parametro_host = hostName, parametro_host_creado = created };
            });
        }
    }
}
