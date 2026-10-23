#nullable disable
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RevitMcpPlugin.Handlers
{
    internal static class AdvancedHelpers
    {
        public const double Mm = 304.8;   // mm por pie

        /// <summary>Material por nombre (sin distinguir mayúsculas); lo crea si no existe.</summary>
        public static ElementId FindOrCreateMaterial(Document doc, string name, out bool created)
        {
            created = false;
            var found = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                .FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (found != null) return found.Id;
            created = true;
            return Material.Create(doc, name);
        }

        /// <summary>Lista de números: acepta un array JSON o texto "50, 75, 110".</summary>
        public static List<double> DoubleList(ToolArgs args, string key)
        {
            var result = new List<double>();
            var t = args.Token(key);
            if (t == null) return result;
            IEnumerable<string> items = t is JArray arr
                ? arr.Select(x => Convert.ToString((x as JValue)?.Value, CultureInfo.InvariantCulture))
                : Convert.ToString(((JValue)t).Value, CultureInfo.InvariantCulture).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var s in items)
            {
                if (!double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                    throw new ToolException($"'{key}' debe ser una lista de números");
                result.Add(d);
            }
            return result;
        }

        public static string Norm(string s) =>
            (s ?? "").Trim().ToLowerInvariant().Replace("á", "a").Replace("é", "e").Replace("í", "i").Replace("ó", "o").Replace("ú", "u")
                .Replace(" ", "").Replace("_", "").Replace("-", "");

        /// <summary>Conectores de un elemento MEP (tuberías, accesorios o familias con MEPModel).</summary>
        public static List<Connector> Connectors(Element e)
        {
            ConnectorSet set = null;
            if (e is MEPCurve c) set = c.ConnectorManager?.Connectors;
            else if (e is FamilyInstance fi) set = fi.MEPModel?.ConnectorManager?.Connectors;
            var list = new List<Connector>();
            if (set != null) foreach (Connector cn in set) list.Add(cn);
            return list;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    public class CaptureViewTool : McpTool
    {
        public override string Name => "capture_view";
        public override string Description =>
            "Captura una vista como PNG y la devuelve como imagen para que puedas verla directamente (sin acceder al disco), " +
            "además del JSON con la ruta del archivo temporal. Sin view_id usa la vista activa. " +
            "Úsala para comprobar visualmente el resultado de lo que modelas.";
        // Solo escribe una imagen temporal en una carpeta fija (no elegida por quien llama) y no toca el modelo: es segura en remoto.
        public override bool ReadOnly => true;
        public override bool RemoteSafe => true;

        public override JObject InputSchema => Schema.Object(
            ("view_id", "integer", "Id de la vista; vacío = vista activa"),
            ("pixeles", "integer", "Tamaño en píxeles del lado mayor (1024 por defecto, máximo 2048)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var view = ViewHelpers.Resolve(args, doc);
            var dir = Path.Combine(Path.GetTempPath(), "RevitMCP", "capturas");
            Directory.CreateDirectory(dir);
            Cleanup(dir);

            var prefix = "vista_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var options = new ImageExportOptions
            {
                FilePath = Path.Combine(dir, prefix),
                FitDirection = FitDirectionType.Horizontal,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ExportRange = ExportRange.SetOfViews,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = (int)Math.Max(256, Math.Min(args.Long("pixeles", 1024), 2048))
            };
            options.SetViewsAndSheets(new List<ElementId> { view.Id });
            doc.ExportImage(options);

            var file = Directory.GetFiles(dir, prefix + "*.png").FirstOrDefault()
                       ?? Directory.GetFiles(dir, prefix + "*").FirstOrDefault();
            if (file == null) throw new ToolException("Revit no generó la imagen (¿la vista admite exportación?)");

            var bytes = File.ReadAllBytes(file);
            return new
            {
                vista = view.Name,
                vista_id = view.Id.GetId(),
                ruta = file,
                bytes = bytes.Length,
                // El servidor saca este campo del JSON y lo envía como contenido MCP de tipo "image".
                _mcp_imagen = new { data = Convert.ToBase64String(bytes), mimeType = "image/png" }
            };
        }

        /// <summary>Borra capturas de más de una hora para que la carpeta temporal no crezca sin límite.</summary>
        private static void Cleanup(string dir)
        {
            try
            {
                foreach (var f in Directory.GetFiles(dir, "vista_*"))
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddHours(-1)) File.Delete(f);
            }
            catch { /* limpiar es opcional */ }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    public class CreateWallTypeWithLayersTool : McpTool
    {
        public override string Name => "create_wall_type_with_layers";
        public override string Description =>
            "Duplica un tipo de muro y le reemplaza la estructura compuesta con las capas indicadas, de exterior a interior " +
            "(p.ej. tarrajeo 15 mm / ladrillo 225 mm / tarrajeo 15 mm). Crea los materiales que no existan. " +
            "funcion: estructura, sustrato, aislamiento, acabado1, acabado2 o membrana; si se omite, las capas de los extremos " +
            "son acabado y las del medio estructura. Devuelve el id del tipo.";
        public override bool ReadOnly => false;

        public override string Example =>
            "{\"nombre\":\"Muro ladrillo 255\",\"tipo_base\":\"Generic - 200mm\",\"capas\":[" +
            "{\"material\":\"Tarrajeo\",\"espesor_mm\":15,\"funcion\":\"acabado1\"}," +
            "{\"material\":\"Ladrillo\",\"espesor_mm\":225,\"funcion\":\"estructura\"}," +
            "{\"material\":\"Tarrajeo\",\"espesor_mm\":15,\"funcion\":\"acabado2\"}]}";

        public override JObject InputSchema => Schema.Object(new[] { "nombre", "capas" },
            ("nombre", "string", "Nombre del nuevo tipo de muro"),
            ("tipo_base", "string", "Tipo de muro a duplicar; vacío = el primer muro básico"),
            ("capas", "object[]", "Capas de exterior a interior: [{material, espesor_mm, funcion}]"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var nombre = args.RequireStr("nombre");
            var capasTok = args.Token("capas") as JArray;
            if (capasTok == null || capasTok.Count == 0) throw new ToolException("'capas' debe ser una lista con al menos una capa [{material, espesor_mm, funcion}]");

            var baseName = args.Str("tipo_base");
            var basics = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().Where(t => t.Kind == WallKind.Basic).ToList();
            var baseType = string.IsNullOrWhiteSpace(baseName)
                ? basics.FirstOrDefault()
                : basics.FirstOrDefault(t => ToolHelpers.SafeName(t).Equals(baseName, StringComparison.OrdinalIgnoreCase));
            if (baseType == null)
                throw new ToolException(string.IsNullOrWhiteSpace(baseName)
                    ? "El proyecto no tiene tipos de muro básicos"
                    : $"Tipo de muro básico '{baseName}' no encontrado. Disponibles: " + string.Join(", ", basics.Take(15).Select(ToolHelpers.SafeName)));
            if (new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().Any(t => ToolHelpers.SafeName(t).Equals(nombre, StringComparison.OrdinalIgnoreCase)))
                throw new ToolException($"Ya existe un tipo de muro llamado '{nombre}'");

            // Se validan las capas antes de tocar el modelo
            var specs = new List<(string material, double mm, string funcion)>();
            for (int i = 0; i < capasTok.Count; i++)
            {
                var o = capasTok[i] as JObject;
                var material = (string)o?["material"];
                if (o == null || string.IsNullOrWhiteSpace(material)) throw new ToolException($"La capa {i + 1} necesita 'material'");
                double mm;
                try { mm = (double)o["espesor_mm"]; } catch { throw new ToolException($"La capa {i + 1} necesita 'espesor_mm' numérico"); }
                if (mm <= 0) throw new ToolException($"La capa {i + 1} debe tener un espesor mayor que 0");
                specs.Add((material.Trim(), mm, (string)o["funcion"]));
            }

            return ToolHelpers.InTransaction(doc, "Crear tipo de muro con capas", () =>
            {
                var newType = baseType.Duplicate(nombre) as WallType;
                var layers = new List<CompoundStructureLayer>();
                var created = new List<string>();
                for (int i = 0; i < specs.Count; i++)
                {
                    var matId = AdvancedHelpers.FindOrCreateMaterial(doc, specs[i].material, out var wasCreated);
                    if (wasCreated) created.Add(specs[i].material);
                    layers.Add(new CompoundStructureLayer(specs[i].mm / AdvancedHelpers.Mm, ParseFunction(specs[i].funcion, i, specs.Count), matId));
                }

                var cs = newType.GetCompoundStructure();
                cs.SetLayers(layers);

                // El núcleo es el tramo de capas estructurales; lo que queda a cada lado son capas de acabado (shell)
                int first = layers.FindIndex(l => l.Function == MaterialFunctionAssignment.Structure);
                int last = layers.FindLastIndex(l => l.Function == MaterialFunctionAssignment.Structure);
                if (first < 0) throw new ToolException("Se necesita al menos una capa con función 'estructura' (el núcleo del muro)");
                cs.SetNumberOfShellLayers(ShellLayerType.Exterior, first);
                cs.SetNumberOfShellLayers(ShellLayerType.Interior, layers.Count - 1 - last);

                IDictionary<int, CompoundStructureError> errors;
                IDictionary<int, int> refs;
                if (!cs.IsValid(doc, out errors, out refs))
                    throw new ToolException("Revit no acepta esa estructura de capas: " +
                        string.Join("; ", errors.Select(e => $"capa {e.Key + 1}: {e.Value}")));

                newType.SetCompoundStructure(cs);
                return new
                {
                    id = newType.Id.GetId(),
                    nombre = newType.Name,
                    base_tipo = baseType.Name,
                    espesor_total_mm = Math.Round(layers.Sum(l => l.Width) * AdvancedHelpers.Mm, 1),
                    capas = specs.Select((s, i) => new { orden = i + 1, material = s.material, espesor_mm = s.mm, funcion = layers[i].Function.ToString() }).ToList(),
                    materiales_creados = created
                };
            });
        }

        private static MaterialFunctionAssignment ParseFunction(string funcion, int index, int count)
        {
            switch (AdvancedHelpers.Norm(funcion))
            {
                case "estructura": case "structure": return MaterialFunctionAssignment.Structure;
                case "sustrato": case "substrate": return MaterialFunctionAssignment.Substrate;
                case "aislamiento": case "insulation": return MaterialFunctionAssignment.Insulation;
                case "acabado1": case "acabado": case "finish1": return MaterialFunctionAssignment.Finish1;
                case "acabado2": case "finish2": return MaterialFunctionAssignment.Finish2;
                case "membrana": case "membrane": return MaterialFunctionAssignment.Membrane;
                case "":
                    if (count > 1 && index == 0) return MaterialFunctionAssignment.Finish1;
                    if (count > 1 && index == count - 1) return MaterialFunctionAssignment.Finish2;
                    return MaterialFunctionAssignment.Structure;
                default:
                    throw new ToolException($"Función de capa '{funcion}' no reconocida. Usa: estructura, sustrato, aislamiento, acabado1, acabado2, membrana");
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    public class AttachWallToRoofTool : McpTool
    {
        public override string Name => "attach_wall_to_roof";
        public override string Description =>
            "Une la parte superior (extremo=\"top\") o inferior (extremo=\"base\") de uno o varios muros a un techo o piso, " +
            "equivalente a «Attach Top/Base» de Revit. Requiere Revit 2025 o superior. Devuelve los muros modificados y los omitidos.";
        public override bool ReadOnly => false;
        public override bool Destructive => true;   // modifica muros existentes

        public override JObject InputSchema => Schema.Object(new[] { "muro_ids", "techo_id" },
            ("muro_ids", "integer[]", "Ids de los muros"),
            ("techo_id", "integer", "Id del techo (o piso) al que se une"),
            ("extremo", "string", "\"top\" (por defecto) o \"base\""));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
#if !REVIT_MODERN
            throw new ToolException("Unir muros a techos por API requiere Revit 2025 o superior");
#else
            var doc = ctx.RequireDoc();
            var wallIds = ModifyHelpers.RequireElements(doc, args, "muro_ids");
            var roof = ToolHelpers.RequireElement(doc, args.RequireLong("techo_id"));
            var location = ParseEnd(args.Str("extremo", "top"));

            var walls = new List<Wall>();
            foreach (var id in wallIds)
            {
                var w = doc.GetElement(id) as Wall;
                if (w == null) throw new ToolException($"El elemento {id.GetId()} no es un muro");
                walls.Add(w);
            }

            return ToolHelpers.InTransaction(doc, "Unir muros a techo", () =>
            {
                var done = new List<object>();
                var skipped = new List<object>();
                foreach (var w in walls)
                {
                    string reason = null;
                    if (!Wall.IsValidTargetAttachment(doc, roof.Id)) reason = "el elemento destino no es válido para unir este muro";
                    else
                    {
                        try { w.AddAttachment(roof.Id, location); }
                        catch (Autodesk.Revit.Exceptions.ApplicationException ex) { reason = ex.Message; }
                    }
                    if (reason == null) done.Add(ToolHelpers.Brief(doc, w));
                    else skipped.Add(new { id = w.Id.GetId(), motivo = reason });
                }
                if (done.Count == 0) throw new ToolException("No se unió ningún muro: " + string.Join("; ", skipped.Select(s => s.ToString())));
                return new { extremo = location.ToString(), techo = ToolHelpers.Brief(doc, roof), modificados = done, omitidos = skipped };
            });
#endif
        }

#if REVIT_MODERN
        private static AttachmentLocation ParseEnd(string s)
        {
            switch (AdvancedHelpers.Norm(s))
            {
                case "top": case "superior": case "arriba": return AttachmentLocation.Top;
                case "base": case "bottom": case "inferior": case "abajo": return AttachmentLocation.Base;
                default: throw new ToolException("'extremo' debe ser \"top\" o \"base\"");
            }
        }
#endif
    }

    // ════════════════════════════════════════════════════════════════════════
    public class CreatePipeTypeTool : McpTool
    {
        public override string Name => "create_pipe_type";
        public override string Description =>
            "Duplica un tipo de tubería y le asigna un segmento del material indicado (PVC, CPVC...) con su tabla de diámetros nominales. " +
            "Crea el material y la lista de tipos (schedule) si no existen. Los accesorios (codos, tes) los hereda del tipo base. " +
            "Los diámetros interior y exterior se estiman a partir del nominal; ajusta la tabla en Revit si necesitas la norma exacta.";
        public override bool ReadOnly => false;

        public override string Example =>
            "{\"nombre\":\"PVC desagüe\",\"tipo_base\":\"Default\",\"material\":\"PVC\",\"diametros_mm\":[50,75,110,160]}";

        public override JObject InputSchema => Schema.Object(new[] { "nombre", "material", "diametros_mm" },
            ("nombre", "string", "Nombre del nuevo tipo de tubería"),
            ("tipo_base", "string", "Tipo de tubería a duplicar; vacío = el primero (get_mep_types los lista)"),
            ("material", "string", "Material del segmento, p.ej. PVC o CPVC"),
            ("diametros_mm", "number[]", "Diámetros nominales en mm, p.ej. [50, 75, 110]"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var nombre = args.RequireStr("nombre");
            var material = args.RequireStr("material").Trim();
            var diametros = AdvancedHelpers.DoubleList(args, "diametros_mm").Distinct().OrderBy(d => d).ToList();
            if (diametros.Count == 0) throw new ToolException("Falta el parámetro obligatorio 'diametros_mm' (lista de diámetros nominales)");
            if (diametros.Any(d => d <= 0)) throw new ToolException("Los diámetros deben ser mayores que 0");

            var baseType = ToolHelpers.FindType<PipeType>(doc, args.Str("tipo_base"), "tipos de tubería");
            if (new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>().Any(t => ToolHelpers.SafeName(t).Equals(nombre, StringComparison.OrdinalIgnoreCase)))
                throw new ToolException($"Ya existe un tipo de tubería llamado '{nombre}'");

            return ToolHelpers.InTransaction(doc, "Crear tipo de tubería", () =>
            {
                var matId = AdvancedHelpers.FindOrCreateMaterial(doc, material, out var matCreated);

                var scheduleName = nombre;
                var schedule = new FilteredElementCollector(doc).OfClass(typeof(PipeScheduleType)).Cast<PipeScheduleType>()
                    .FirstOrDefault(s => ToolHelpers.SafeName(s).Equals(scheduleName, StringComparison.OrdinalIgnoreCase))
                    ?? PipeScheduleType.Create(doc, scheduleName);

                // Tabla de tamaños: interior = nominal; el espesor de pared se estima como el 6 % del nominal (mínimo 2 mm)
                var sizes = diametros.Select(d =>
                {
                    double wall = Math.Max(2.0, d * 0.06);
                    return new MEPSize(d / AdvancedHelpers.Mm, d / AdvancedHelpers.Mm, (d + 2 * wall) / AdvancedHelpers.Mm, true, true);
                }).ToList();
                var segment = PipeSegment.Create(doc, matId, schedule.Id, sizes);

                var pipeType = baseType.Duplicate(nombre) as PipeType;
                var mgr = pipeType.RoutingPreferenceManager;
                for (int i = mgr.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments) - 1; i >= 0; i--)
                    mgr.RemoveRule(RoutingPreferenceRuleGroupType.Segments, i);
                mgr.AddRule(RoutingPreferenceRuleGroupType.Segments, new RoutingPreferenceRule(segment.Id, nombre));

                return new
                {
                    id = pipeType.Id.GetId(),
                    nombre = pipeType.Name,
                    base_tipo = baseType.Name,
                    material,
                    segmento_id = segment.Id.GetId(),
                    diametros_mm = diametros,
                    material_creado = matCreated,
                    aviso = "Diámetros interior y exterior estimados (pared ≈ 6 % del nominal); revisa Gestionar ▸ Configuración MEP ▸ Segmentos si necesitas la norma exacta."
                };
            });
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    public class CreatePipeFittingConnectTool : McpTool
    {
        public override string Name => "create_pipe_fitting_connect";
        public override string Description =>
            "Conecta dos tuberías con el accesorio que corresponda: unión si están alineadas, codo si sus ejes se cortan en sus extremos, " +
            "te si el extremo de una llega a la mitad de la otra (la tubería atravesada se parte). " +
            "Las tuberías deben estar en el mismo plano y sus ejes deben cortarse (con tolerancia de ~9 mm).";
        public override bool ReadOnly => false;
        public override bool Destructive => true;   // puede partir una tubería existente

        public override JObject InputSchema => Schema.Object(new[] { "pipe_id_a", "pipe_id_b" },
            ("pipe_id_a", "integer", "Id de la primera tubería"),
            ("pipe_id_b", "integer", "Id de la segunda tubería"));

        private const double Tol = 0.03;   // pies (~9 mm)

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var a = ToolHelpers.RequireElement(doc, args.RequireLong("pipe_id_a")) as Pipe;
            var b = ToolHelpers.RequireElement(doc, args.RequireLong("pipe_id_b")) as Pipe;
            if (a == null || b == null) throw new ToolException("Los dos ids deben ser tuberías (Pipe)");
            if (a.Id == b.Id) throw new ToolException("Indica dos tuberías distintas");

            var la = (a.Location as LocationCurve)?.Curve as Line;
            var lb = (b.Location as LocationCurve)?.Curve as Line;
            if (la == null || lb == null) throw new ToolException("Solo se pueden conectar tuberías rectas");

            XYZ pa, pb;
            double s, t;
            bool parallel = Closest(la, lb, out pa, out pb, out s, out t);

            return ToolHelpers.InTransaction(doc, "Conectar tuberías con accesorio", () =>
            {
                if (parallel)
                {
                    if (lb.Distance(la.Origin) > Tol) throw new ToolException("Las tuberías son paralelas y no están alineadas: no se pueden unir");
                    var (ca, cb) = NearestFreePair(a, b);
                    var fit = doc.Create.NewUnionFitting(ca, cb);
                    return Result(fit, "unión", a, b, null);
                }

                if (pa.DistanceTo(pb) > Tol)
                    throw new ToolException($"Los ejes no se cortan: quedan a {Units.M(pa.DistanceTo(pb)) * 1000:0} mm entre sí (¿no están en el mismo plano?)");

                bool interiorA = s > Tol && s < la.Length - Tol;
                bool interiorB = t > Tol && t < lb.Length - Tol;
                if (interiorA && interiorB)
                    throw new ToolException("Las tuberías se cruzan por el interior de ambas: un cruce no se resuelve con un accesorio de este tipo");

                if (!interiorA && !interiorB)
                {
                    var ca = NearestFree(a, pa);
                    var cb = NearestFree(b, pb);
                    var fit = doc.Create.NewElbowFitting(ca, cb);
                    return Result(fit, "codo", a, b, null);
                }

                // Te: la tubería atravesada (run) se parte en el punto de encuentro y la otra es la derivación
                var run = interiorA ? a : b;
                var branch = interiorA ? b : a;
                var point = interiorA ? pa : pb;
                var branchConn = NearestFree(branch, point);

                var newId = PlumbingUtils.BreakCurve(doc, run.Id, point);
                doc.Regenerate();
                var runB = doc.GetElement(newId) as Pipe;
                var c1 = NearestFree(run, point);
                var c2 = NearestFree(runB, point);
                var tee = doc.Create.NewTeeFitting(c1, c2, branchConn);
                return Result(tee, "te", a, b, runB.Id.GetId());
            });
        }

        private static object Result(FamilyInstance fitting, string tipo, Pipe a, Pipe b, long? nuevaTuberia) => new
        {
            accesorio_id = fitting.Id.GetId(),
            tipo,
            familia = fitting.Symbol?.FamilyName ?? "",
            tuberias = new[] { a.Id.GetId(), b.Id.GetId() },
            tuberia_nueva_por_division = nuevaTuberia
        };

        /// <summary>Puntos más cercanos entre las rectas infinitas. Devuelve true si son paralelas.</summary>
        private static bool Closest(Line la, Line lb, out XYZ pa, out XYZ pb, out double s, out double t)
        {
            var d1 = la.Direction; var d2 = lb.Direction;
            var w0 = la.Origin - lb.Origin;
            double bb = d1.DotProduct(d2), d = d1.DotProduct(w0), e = d2.DotProduct(w0);
            double denom = 1 - bb * bb;
            if (Math.Abs(denom) < 1e-9) { pa = pb = null; s = t = 0; return true; }
            s = (bb * e - d) / denom;
            t = (e - bb * d) / denom;
            pa = la.Origin + s * d1;
            pb = lb.Origin + t * d2;
            return false;
        }

        private static Connector NearestFree(Element pipe, XYZ point)
        {
            var c = AdvancedHelpers.Connectors(pipe).OrderBy(x => x.Origin.DistanceTo(point)).First();
            if (c.IsConnected) throw new ToolException($"El extremo de la tubería {pipe.Id.GetId()} cercano al punto de encuentro ya está conectado");
            return c;
        }

        private static (Connector, Connector) NearestFreePair(Pipe a, Pipe b)
        {
            var best = (ca: (Connector)null, cb: (Connector)null, d: double.MaxValue);
            foreach (var x in AdvancedHelpers.Connectors(a).Where(c => !c.IsConnected))
                foreach (var y in AdvancedHelpers.Connectors(b).Where(c => !c.IsConnected))
                {
                    var d = x.Origin.DistanceTo(y.Origin);
                    if (d < best.d) best = (x, y, d);
                }
            if (best.ca == null) throw new ToolException("No hay extremos libres para conectar");
            return (best.ca, best.cb);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    public class PlacePlumbingFixtureTool : McpTool
    {
        public override string Name => "place_plumbing_fixture";
        public override string Description =>
            "Coloca un aparato sanitario (familia de la categoría Aparatos sanitarios, basada en nivel) en el punto (x, y, z) en metros " +
            "y asigna sus conectores de tubería a sistemas nuevos de los tipos indicados: sistema_agua_fria a los conectores de agua fría " +
            "doméstica y sistema_sanitario a los de desagüe. Los nombres de sistema salen de get_mep_types.";
        public override bool ReadOnly => false;

        public override JObject InputSchema => Schema.Object(new[] { "familia", "x", "y" },
            ("familia", "string", "Nombre de la familia, p.ej. \"Inodoro\""),
            ("tipo", "string", "Nombre del tipo; vacío = el primero de la familia"),
            ("x", "number", "X (m)"), ("y", "number", "Y (m)"),
            ("z", "number", "Altura sobre el nivel (m, 0 por defecto)"),
            ("rotacion_grados", "number", "Giro alrededor del eje vertical en grados (0 por defecto)"),
            ("nivel", "string", "Nombre del nivel; vacío = el más bajo"),
            ("sistema_agua_fria", "string", "Tipo de sistema de tuberías para agua fría (opcional)"),
            ("sistema_sanitario", "string", "Tipo de sistema de tuberías sanitario (opcional)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var familia = args.RequireStr("familia");
            var tipo = args.Str("tipo");
            var level = ToolHelpers.FindLevel(doc, args.Str("nivel"));
            var point = new XYZ(args.RequireDbl("x") / Units.FeetToMeters, args.RequireDbl("y") / Units.FeetToMeters,
                level.Elevation + args.Dbl("z", 0) / Units.FeetToMeters);
            var radians = args.Dbl("rotacion_grados", 0) * Math.PI / 180.0;

            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.FamilyName.Equals(familia, StringComparison.OrdinalIgnoreCase)).ToList();
            if (symbols.Count == 0) throw new ToolException($"Familia '{familia}' no cargada en el proyecto (usa get_families)");
            var symbol = tipo == "" ? symbols[0] : symbols.FirstOrDefault(s => s.Name.Equals(tipo, StringComparison.OrdinalIgnoreCase));
            if (symbol == null) throw new ToolException($"Tipo '{tipo}' no existe en la familia '{familia}'. Disponibles: " + string.Join(", ", symbols.Select(s => s.Name)));
            if (symbol.Category == null || symbol.Category.Id.GetId() != (long)BuiltInCategory.OST_PlumbingFixtures)
                throw new ToolException($"'{familia}' no es de la categoría Aparatos sanitarios; usa place_family_instance para otras familias");

            var coldType = args.Has("sistema_agua_fria") ? FindSystemType(doc, args.Str("sistema_agua_fria")) : null;
            var sanType = args.Has("sistema_sanitario") ? FindSystemType(doc, args.Str("sistema_sanitario")) : null;

            return ToolHelpers.InTransaction(doc, "Colocar aparato sanitario", () =>
            {
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }

                FamilyInstance inst;
                try { inst = doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural); }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                    throw new ToolException($"La familia '{familia}' necesita un host (muro, cara o techo) y no se puede colocar solo con coordenadas");
                }

                if (Math.Abs(radians) > 1e-9)
                    ElementTransformUtils.RotateElement(doc, inst.Id, Line.CreateBound(point, point + XYZ.BasisZ), radians);

                var avisos = new List<string>();
                var sistemas = new List<object>();
                AssignSystem(doc, inst, coldType, PipeSystemType.DomesticColdWater, "agua fría", sistemas, avisos);
                AssignSystem(doc, inst, sanType, PipeSystemType.Sanitary, "sanitario", sistemas, avisos);

                var conectores = AdvancedHelpers.Connectors(inst).Where(c => c.Domain == Domain.DomainPiping)
                    .Select(c => new { indice = c.Id, sistema = c.PipeSystemType.ToString(), conectado = c.IsConnected }).ToList();
                return new
                {
                    id = inst.Id.GetId(),
                    familia = symbol.FamilyName,
                    tipo = symbol.Name,
                    nivel = level.Name,
                    rotacion_grados = args.Dbl("rotacion_grados", 0),
                    conectores,
                    sistemas,
                    avisos
                };
            });
        }

        private static PipingSystemType FindSystemType(Document doc, string name)
        {
            var types = new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().ToList();
            return types.FirstOrDefault(t => ToolHelpers.SafeName(t).Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ToolException($"Tipo de sistema '{name}' no encontrado. Disponibles: " + string.Join(", ", types.Take(15).Select(ToolHelpers.SafeName)));
        }

        /// <summary>Crea un sistema del tipo dado y le añade los conectores del aparato con esa clasificación.</summary>
        private static void AssignSystem(Document doc, FamilyInstance inst, PipingSystemType type, PipeSystemType classification,
            string etiqueta, List<object> sistemas, List<string> avisos)
        {
            if (type == null) return;
            var matching = AdvancedHelpers.Connectors(inst)
                .Where(c => c.Domain == Domain.DomainPiping && c.PipeSystemType == classification && !c.IsConnected).ToList();
            if (matching.Count == 0)
            {
                avisos.Add($"La familia no tiene conectores de {etiqueta} libres: no se asignó '{ToolHelpers.SafeName(type)}'");
                return;
            }
            try
            {
                var system = PipingSystem.Create(doc, type.Id);
                var set = new ConnectorSet();
                foreach (var c in matching) set.Insert(c);
                system.Add(set);
                sistemas.Add(new { sistema_id = system.Id.GetId(), tipo = ToolHelpers.SafeName(type), conectores = matching.Count });
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                avisos.Add($"No se pudo asignar el sistema '{ToolHelpers.SafeName(type)}' a los conectores de {etiqueta}: {ex.Message}");
            }
        }
    }
}
