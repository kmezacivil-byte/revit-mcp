#nullable disable
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMcpPlugin.Families
{
    public class CreateReferencePlaneTool : McpTool
    {
        public override string Name => "create_reference_plane";
        public override string Description =>
            "Crea un plano de referencia en una familia abierta. La normal debe estar alineada con X, Y o Z; " +
            "origen es un punto del plano en metros. 'referencia' define cómo lo usan las instancias: " +
            $"{string.Join(", ", FamilyGeo.StrengthNames)} (fuerte por defecto no se cambia si se omite).";
        public override bool ReadOnly => false;
        public override string Example => "{\"familia_doc_id\":1,\"nombre\":\"Plano_Inicio\",\"origen\":[0,0,0],\"normal\":[0,1,0],\"referencia\":\"fuerte\"}";

        public override JObject InputSchema => Schema.Object(new[] { "familia_doc_id", "nombre", "origen", "normal" },
            ("familia_doc_id", "integer", "Id devuelto por create_family"),
            ("nombre", "string", "Nombre único del plano"),
            ("origen", "number[]", "Punto del plano [x, y, z] en metros"),
            ("normal", "number[]", "Normal alineada con un eje, p.ej. [0,1,0]"),
            ("referencia", "string", "Tipo de referencia (opcional)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var s = FamilySessions.Require(args);
            var o = args.Vec3("origen");
            var n = args.Vec3("normal");
            int idx = n.Select((v, i) => new { v = Math.Abs(v), i }).OrderByDescending(x => x.v).First().i;
            if (Math.Abs(n[idx]) < 1e-9 || n.Where((v, i) => i != idx).Any(v => Math.Abs(v) > 1e-9))
                throw new ToolException("'normal' debe estar alineada con X, Y o Z, p.ej. [1,0,0], [0,1,0] o [0,0,1]");
            char axis = "XYZ"[idx];

            return ToolHelpers.InTransaction(s.Doc, "Crear plano de referencia", () =>
            {
                var plane = FamilyGeo.CreatePlane(s.Doc, args.RequireStr("nombre"), axis, o[idx] / Units.FeetToMeters, args.Str("referencia", null));
                return new
                {
                    plano = plane.Name,
                    id = plane.Id.GetId(),
                    normal = axis.ToString(),
                    posicion_m = Units.M(FamilyGeo.CoordinateOf(plane))
                };
            });
        }
    }

    public class CreateFamilyDimensionTool : McpTool
    {
        public override string Name => "create_family_dimension";
        public override string Description =>
            "Crea una cota entre planos de referencia paralelos de una familia y, si se indica 'parametro', la etiqueta con ese " +
            "parámetro de longitud: la distancia entre los planos pasa a seguir al parámetro. Con 3 o más planos y igual=true " +
            "crea una cota EQ (equidistantes). bloquear=true fija la cota.";
        public override bool ReadOnly => false;
        public override string Example => "{\"familia_doc_id\":1,\"planos\":[\"Plano_Izq\",\"Plano_Der\"],\"parametro\":\"Ancho\"}";

        public override JObject InputSchema => Schema.Object(new[] { "familia_doc_id", "planos" },
            ("familia_doc_id", "integer", "Id devuelto por create_family"),
            ("planos", "string[]", "Nombres de los planos de referencia (mínimo 2, paralelos)"),
            ("parametro", "string", "Parámetro de longitud con el que etiquetar la cota (solo con 2 planos)"),
            ("igual", "boolean", "Cota EQ entre 3 o más planos (false por defecto)"),
            ("bloquear", "boolean", "Bloquear la cota (false por defecto)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var s = FamilySessions.Require(args);
            var names = args.StrList("planos");
            if (names.Count < 2) throw new ToolException("'planos' necesita al menos 2 nombres de planos de referencia");
            bool igual = args.Bool("igual", false);
            var parametro = args.Str("parametro");
            if (parametro != "" && names.Count != 2) throw new ToolException("Solo se puede etiquetar con un parámetro una cota de exactamente 2 planos");
            if (igual && names.Count < 3) throw new ToolException("igual=true necesita 3 o más planos");

            return ToolHelpers.InTransaction(s.Doc, "Crear cota de familia", () =>
            {
                var planes = names.Select(n => FamilyGeo.FindPlane(s.Doc, n)).ToList();
                var axis = FamilyGeo.NormalAxisOf(planes[0]);
                var dim = FamilyGeo.LinearDimension(s.Doc, FamilyGeo.PlaneCreationView(s.Doc, axis), planes, 0);
                if (parametro != "")
                {
                    var fp = FamilyOps.RequireParam(s.Doc.FamilyManager, parametro);
                    if (FamilyValues.KindOf(fp) != ParamKind.Length) throw new ToolException($"El parámetro '{parametro}' no es de tipo Longitud");
                    dim.FamilyLabel = fp;
                }
                if (igual) dim.AreSegmentsEqual = true;
                if (args.Bool("bloquear", false)) dim.IsLocked = true;
                s.Doc.Regenerate();
                return new
                {
                    cota_id = dim.Id.GetId(),
                    planos = names,
                    etiqueta = parametro == "" ? null : parametro,
                    valor_m = dim.Value.HasValue ? Units.M(dim.Value.Value) : (double?)null,
                    igual,
                    bloqueada = dim.IsLocked
                };
            });
        }
    }

    public class CreateExtrusionTool : McpTool
    {
        public override string Name => "create_extrusion";
        public override string Description =>
            "Crea una extrusión (sólida o vacía) en una familia abierta con un perfil predefinido cuyas medidas siguen a parámetros. " +
            "perfil = {forma:..., ...}. Formas: " + ProfileFactory.Help + " 'eje' es la dirección de extrusión (X, Y o Z). " +
            "'desde' y 'hasta' son la posición inicial y final a lo largo del eje: metros o nombre de un parámetro de longitud. " +
            "Crea los planos de referencia, cotas etiquetadas y alineaciones bloqueadas necesarias; en 'advertencias' informa de " +
            "cualquier parte que no quedó parametrizada. 'material' puede ser un parámetro de tipo Material o el nombre de un material.";
        public override bool ReadOnly => false;
        public override string Example =>
            "{\"familia_doc_id\":1,\"nombre\":\"Tubo\",\"perfil\":{\"forma\":\"anillo\",\"diametro_ext\":\"Diametro_Ext\",\"espesor\":\"Espesor\"},\"eje\":\"Y\",\"desde\":0,\"hasta\":\"Longitud\"}";

        public override JObject InputSchema => Schema.Object(new[] { "familia_doc_id", "nombre", "perfil", "eje", "desde", "hasta" },
            ("familia_doc_id", "integer", "Id devuelto por create_family"),
            ("nombre", "string", "Nombre único de la pieza"),
            ("perfil", "object", "Perfil: {forma, medidas...} (ver descripción)"),
            ("eje", "string", "Dirección de extrusión: X, Y o Z"),
            ("desde", "string", "Inicio a lo largo del eje: metros o parámetro (p.ej. 0)"),
            ("hasta", "string", "Fin a lo largo del eje: metros o parámetro (p.ej. \"Longitud\")"),
            ("es_vacio", "boolean", "true = vacío (corta otras piezas); false = sólido (por defecto)"),
            ("subcategoria", "string", "Subcategoría de la pieza (se crea si no existe)"),
            ("material", "string", "Parámetro de material o nombre de material"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var s = FamilySessions.Require(args);
            return ToolHelpers.InTransaction(s.Doc, "Crear extrusión de familia", () =>
                FamilyForms.CreateExtrusion(s, args.RequireStr("nombre"), args.RequireObj("perfil"),
                    FamilyGeo.ParseAxis(args.RequireStr("eje"), "eje"), args.Str("desde"), args.Str("hasta"),
                    args.Bool("es_vacio", false), args.Str("subcategoria"), args.Str("material")));
        }
    }

    public class CreateSweepTool : McpTool
    {
        public override string Name => "create_sweep";
        public override string Description =>
            "Crea un barrido (sólido o vacío): un perfil circulo{diametro} o rectangulo{ancho,alto} recorre un camino de puntos " +
            "[[x,y,z],...] en metros (coplanares); sirve para caminos verticales, horizontales e inclinados (p.ej. diagonales a 45°). " +
            "Las medidas son fijas; no siguen parámetros. En el rectángulo, 'ancho' queda en el plano que contiene al camino cuando este es recto " +
            "y vertical; usa giro_perfil_grados para girar el perfil sobre el camino.";
        public override bool ReadOnly => false;
        public override string Example => "{\"familia_doc_id\":1,\"nombre\":\"Tubo_codo\",\"camino\":[[0,0,0],[1,0,0],[1,1,0]],\"perfil\":{\"forma\":\"circulo\",\"diametro\":\"50 mm\"}}";

        public override JObject InputSchema => Schema.Object(new[] { "familia_doc_id", "nombre", "camino", "perfil" },
            ("familia_doc_id", "integer", "Id devuelto por create_family"),
            ("nombre", "string", "Nombre único de la pieza"),
            ("camino", "number[][]", "Puntos del camino [[x,y,z],...] en metros"),
            ("perfil", "object", "{forma:\"circulo\",diametro} o {forma:\"rectangulo\",ancho,alto}"),
            ("giro_perfil_grados", "number", "Giro del perfil sobre el camino en grados (0 por defecto)"),
            ("es_vacio", "boolean", "true = vacío (false por defecto)"),
            ("subcategoria", "string", "Subcategoría (opcional)"),
            ("material", "string", "Parámetro de material o nombre de material"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var s = FamilySessions.Require(args);
            return ToolHelpers.InTransaction(s.Doc, "Crear barrido de familia", () =>
                FamilyForms.CreateSweep(s, args.RequireStr("nombre"), args.Token("camino"), args.RequireObj("perfil"),
                    args.Bool("es_vacio", false), args.Str("subcategoria"), args.Str("material"), args.Dbl("giro_perfil_grados", 0)));
        }
    }

    public class CreateRevolutionTool : McpTool
    {
        public override string Name => "create_revolution";
        public override string Description =>
            "Crea una revolución (sólida o vacía): un perfil en un plano (XY, XZ o YZ) gira alrededor de un eje de ese plano. " +
            "Perfil: perfil_puntos [[a,b],...] en metros, o circulo {centro:[a,b],diametro}. eje = [[a1,b1],[a2,b2]]. " +
            "Los ángulos (grados, o nombre de un parámetro de ángulo) pueden seguir parámetros; el perfil y el eje son fijos.";
        public override bool ReadOnly => false;
        public override string Example => "{\"familia_doc_id\":1,\"nombre\":\"Cono\",\"plano\":\"XZ\",\"perfil_puntos\":[[0,0],[0.1,0],[0,0.3]],\"eje\":[[0,0],[0,1]],\"angulo_inicio\":0,\"angulo_fin\":360}";

        public override JObject InputSchema => Schema.Object(new[] { "familia_doc_id", "nombre", "plano", "eje", "angulo_inicio", "angulo_fin" },
            ("familia_doc_id", "integer", "Id devuelto por create_family"),
            ("nombre", "string", "Nombre único de la pieza"),
            ("plano", "string", "Plano del perfil: XY, XZ o YZ"),
            ("perfil_puntos", "number[][]", "Contorno cerrado [[a,b],...] en metros"),
            ("circulo", "object", "Alternativa: {centro:[a,b],diametro}"),
            ("eje", "number[][]", "Eje de giro [[a1,b1],[a2,b2]] en metros, en el plano del perfil"),
            ("angulo_inicio", "string", "Grados o parámetro de ángulo"),
            ("angulo_fin", "string", "Grados o parámetro de ángulo"),
            ("es_vacio", "boolean", "true = vacío (false por defecto)"),
            ("subcategoria", "string", "Subcategoría (opcional)"),
            ("material", "string", "Parámetro de material o nombre de material"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var s = FamilySessions.Require(args);
            return ToolHelpers.InTransaction(s.Doc, "Crear revolución de familia", () =>
                FamilyForms.CreateRevolution(s, args.RequireStr("nombre"), args.RequireStr("plano"), args.Token("perfil_puntos"),
                    args.Obj("circulo"), args.Token("eje"), args.Str("angulo_inicio"), args.Str("angulo_fin"),
                    args.Bool("es_vacio", false), args.Str("subcategoria"), args.Str("material")));
        }
    }

    public class AlignToPlaneTool : McpTool
    {
        public override string Name => "align_to_plane";
        public override string Description =>
            "Alinea la cara inicial o final de una extrusión a un plano de referencia (normal igual al eje de la extrusión) y, con " +
            "bloquear=true, la bloquea: así la pieza sigue al plano cuando este se mueve con un parámetro.";
        public override bool ReadOnly => false;
        public override string Example => "{\"familia_doc_id\":1,\"pieza\":\"Tubo\",\"cara\":\"fin\",\"plano\":\"Plano_Fin\",\"bloquear\":true}";

        public override JObject InputSchema => Schema.Object(new[] { "familia_doc_id", "pieza", "cara", "plano" },
            ("familia_doc_id", "integer", "Id devuelto por create_family"),
            ("pieza", "string", "Nombre de la extrusión"),
            ("cara", "string", "\"inicio\" o \"fin\""),
            ("plano", "string", "Nombre del plano de referencia"),
            ("bloquear", "boolean", "Bloquear la alineación (true por defecto)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var s = FamilySessions.Require(args);
            var piece = args.RequireStr("pieza");
            if (!s.Forms.TryGetValue(piece, out var form) || form.Kind != "extrusion")
                throw new ToolException($"No hay una extrusión llamada '{piece}' (usa list_family_elements)");
            var cara = FamilyHelpers.Normalize(args.RequireStr("cara"));
            if (cara != "inicio" && cara != "fin") throw new ToolException("'cara' debe ser \"inicio\" o \"fin\"");

            return ToolHelpers.InTransaction(s.Doc, "Alinear pieza a plano", () =>
            {
                var plane = FamilyGeo.FindPlane(s.Doc, args.RequireStr("plano"));
                if (FamilyGeo.NormalAxisOf(plane) != form.Axis)
                    throw new ToolException($"El plano '{plane.Name}' debe tener la misma normal que el eje de la extrusión ({form.Axis})");

                var view = form.Axis == 'Z' ? FamilyGeo.ElevationLookingAlong(s.Doc, 'Y') : FamilyGeo.PlanView(s.Doc);
                var ext = (Extrusion)s.Doc.GetElement(form.Id);
                var options = new Options { ComputeReferences = true, View = view };
                var axisVec = FamilyGeo.AxisVector(form.Axis);

                var faces = new List<PlanarFace>();
                foreach (var obj in ext.get_Geometry(options))
                    if (obj is Solid solid)
                        foreach (Face f in solid.Faces)
                            if (f is PlanarFace pf && Math.Abs(pf.FaceNormal.DotProduct(axisVec)) > 0.999) faces.Add(pf);
                if (faces.Count == 0) throw new ToolException("No se encontraron caras de extremo en la extrusión");

                var face = (cara == "inicio" ? faces.OrderBy(f => FamilyGeo.Comp(f.Origin, form.Axis)) : faces.OrderByDescending(f => FamilyGeo.Comp(f.Origin, form.Axis))).First();
                Dimension dim;
                try { dim = s.Doc.FamilyCreate.NewAlignment(view, plane.GetReference(), face.Reference); }
                catch (Exception ex) { throw new ToolException($"Revit no pudo alinear la cara '{cara}' de '{piece}' con '{plane.Name}': {ex.Message}"); }
                if (args.Bool("bloquear", true)) dim.IsLocked = true;
                s.Doc.Regenerate();
                return new { pieza = piece, cara, plano = plane.Name, bloqueada = dim.IsLocked };
            });
        }
    }

    public class AssociateFamilyMaterialTool : McpTool
    {
        public override string Name => "associate_family_material";
        public override string Description =>
            "Vincula el material de una pieza de la familia a un parámetro de tipo Material (lo crea si no existe), para poder " +
            "cambiarlo por tipo desde el proyecto.";
        public override bool ReadOnly => false;
        public override string Example => "{\"familia_doc_id\":1,\"pieza\":\"Tubo\",\"parametro_material\":\"Material_Tubo\"}";

        public override JObject InputSchema => Schema.Object(new[] { "familia_doc_id", "pieza", "parametro_material" },
            ("familia_doc_id", "integer", "Id devuelto por create_family"),
            ("pieza", "string", "Nombre de la pieza"),
            ("parametro_material", "string", "Nombre del parámetro de material"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var s = FamilySessions.Require(args);
            var piece = args.RequireStr("pieza");
            if (!s.Forms.TryGetValue(piece, out var form)) throw new ToolException($"No hay una pieza llamada '{piece}' (usa list_family_elements)");

            return ToolHelpers.InTransaction(s.Doc, "Asociar material", () =>
            {
                var fm = s.Doc.FamilyManager;
                var name = args.RequireStr("parametro_material");
                var fp = fm.get_Parameter(name) ?? FamilyOps.AddParameter(s.Doc, name, "Material", "Materiales", false);
                if (FamilyValues.KindOf(fp) != ParamKind.Material) throw new ToolException($"El parámetro '{name}' no es de tipo Material");
                var el = s.Doc.GetElement(form.Id);
                var p = el.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM) ?? throw new ToolException("La pieza no admite material");
                fm.AssociateElementParameterToFamilyParameter(p, fp);
                return new { pieza = piece, parametro_material = name };
            });
        }
    }

    public class ListFamilyElementsTool : McpTool
    {
        public override string Name => "list_family_elements";
        public override string Description =>
            "Lista las piezas creadas (extrusiones, barridos, revoluciones, instancias anidadas) y los planos de referencia de una familia abierta.";
        public override string Example => "{\"familia_doc_id\":1}";

        public override JObject InputSchema => Schema.Object(new[] { "familia_doc_id" },
            ("familia_doc_id", "integer", "Id devuelto por create_family"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var s = FamilySessions.Require(args);
            var planes = new FilteredElementCollector(s.Doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                .Where(p => p.Name != "")
                .Select(p => { string n; double pos; try { n = FamilyGeo.NormalAxisOf(p).ToString(); pos = Units.M(FamilyGeo.CoordinateOf(p)); } catch { n = "?"; pos = 0; } return new { nombre = p.Name, normal = n, posicion_m = pos }; })
                .OrderBy(p => p.nombre).ToList();
            return new
            {
                familia_doc_id = s.Id,
                piezas = s.Forms.Select(kv => new { nombre = kv.Key, tipo = kv.Value.Kind, eje = kv.Value.Axis == ' ' ? null : kv.Value.Axis.ToString(), id = kv.Value.Id.GetId() }),
                planos = planes
            };
        }
    }
}
