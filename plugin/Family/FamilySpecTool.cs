#nullable disable
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RevitMcpPlugin.Families
{
    /// <summary>
    /// Construye una familia completa a partir de un JSON: parámetros, planos, piezas, tipos y guardado.
    /// Orquesta las mismas tools de bajo nivel (cada paso es una transacción propia), así que el comportamiento es idéntico.
    /// </summary>
    public class BuildFamilyFromSpecTool : McpTool
    {
        public override string Name => "build_family_from_spec";
        public override string Description =>
            "Construye una familia paramétrica completa desde un JSON y la guarda como .rfa. Spec: {nombre, categoria, plantilla, " +
            "parametros:[{nombre,tipo (Longitud|Angulo|Numero|Entero|Texto|Material|SiNo),instancia,valor,formula,grupo}], " +
            "planos_referencia:[{nombre,origen:[x,y,z],normal:[x,y,z],referencia}], " +
            "piezas:[{tipo:extrusion|revolucion|barrido|anidada, nombre, ...los mismos campos que create_extrusion / create_revolution / " +
            "create_sweep / nest_family}], tipos:[{nombre,valores:{parametro:valor}}], guardar_en, sobrescribir}. " +
            "Longitudes en metros (o \"125 mm\"), ángulos en grados. Valida todo el spec antes de crear nada y devuelve los errores por campo; " +
            "con validar_solamente=true solo valida. Si un paso falla, la familia incompleta se descarta (conservar_si_falla=true la deja abierta).";
        public override bool ReadOnly => false;
        public override string Example =>
            "{\"spec\":{\"nombre\":\"Tubo hueco\",\"categoria\":\"modelo_generico\",\"parametros\":[{\"nombre\":\"Diametro_Ext\",\"tipo\":\"Longitud\"},{\"nombre\":\"Espesor\",\"tipo\":\"Longitud\"},{\"nombre\":\"Longitud\",\"tipo\":\"Longitud\"}],\"piezas\":[{\"tipo\":\"extrusion\",\"nombre\":\"Tubo\",\"perfil\":{\"forma\":\"anillo\",\"diametro_ext\":\"Diametro_Ext\",\"espesor\":\"Espesor\"},\"eje\":\"Y\",\"desde\":0,\"hasta\":\"Longitud\"}],\"tipos\":[{\"nombre\":\"125x5\",\"valores\":{\"Diametro_Ext\":\"125 mm\",\"Espesor\":\"5 mm\",\"Longitud\":2}},{\"nombre\":\"100x5\",\"valores\":{\"Diametro_Ext\":\"100 mm\",\"Espesor\":\"5 mm\",\"Longitud\":2}}],\"guardar_en\":\"C:/Familias/Tubo_hueco.rfa\"}}";

        public override JObject InputSchema => Schema.Object(new[] { "spec" },
            ("spec", "object", "Descripción de la familia (ver descripción de la tool)"),
            ("validar_solamente", "boolean", "Solo validar el spec, sin crear nada (false por defecto)"),
            ("conservar_si_falla", "boolean", "Si un paso falla, dejar la familia abierta para inspeccionarla (false por defecto)"));

        private static readonly string[] PieceTypes = { "extrusion", "revolucion", "barrido", "anidada" };

        private static string Text(JToken t) => t == null ? null : Convert.ToString((t as JValue)?.Value, CultureInfo.InvariantCulture);

        private static JObject Clone(JObject o, params string[] drop)
        {
            var c = (JObject)o.DeepClone();
            foreach (var d in drop) c.Remove(d);
            return c;
        }

        // ── Validación ─────────────────────────────────────────────────────────
        internal static List<string> Validate(JObject spec)
        {
            var errors = new List<string>();
            void Err(string path, string msg) => errors.Add($"{path}: {msg}");

            if (string.IsNullOrWhiteSpace(Text(spec["nombre"]))) Err("nombre", "es obligatorio");

            var paramNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parametros = spec["parametros"] as JArray;
            if (spec["parametros"] != null && parametros == null) Err("parametros", "debe ser una lista");
            if (parametros != null)
                for (int i = 0; i < parametros.Count; i++)
                {
                    var p = parametros[i] as JObject;
                    var path = $"parametros[{i}]";
                    if (p == null) { Err(path, "debe ser un objeto"); continue; }
                    var n = Text(p["nombre"]);
                    if (string.IsNullOrWhiteSpace(n)) Err(path + ".nombre", "es obligatorio");
                    else if (!paramNames.Add(n)) Err(path + ".nombre", $"'{n}' está repetido");
                    try { FamilyValues.CanonicalType(Text(p["tipo"]) ?? ""); }
                    catch (ToolException ex) { Err(path + ".tipo", ex.Message); }
                    if (p["formula"] != null && p["valor"] != null) Err(path, "no puede tener 'formula' y 'valor' a la vez");
                }

            var planeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var planos = spec["planos_referencia"] as JArray;
            if (planos != null)
                for (int i = 0; i < planos.Count; i++)
                {
                    var p = planos[i] as JObject;
                    var path = $"planos_referencia[{i}]";
                    if (p == null) { Err(path, "debe ser un objeto"); continue; }
                    var n = Text(p["nombre"]);
                    if (string.IsNullOrWhiteSpace(n)) Err(path + ".nombre", "es obligatorio");
                    else if (!planeNames.Add(n)) Err(path + ".nombre", $"'{n}' está repetido");
                    foreach (var key in new[] { "origen", "normal" })
                        if (!(p[key] is JArray a) || a.Count != 3 || a.Any(x => x.Type != JTokenType.Integer && x.Type != JTokenType.Float))
                            Err($"{path}.{key}", "debe ser una lista de 3 números [x, y, z]");
                }

            bool IsKnownParam(string v) => FamilyValues.IsNumeric(v) || paramNames.Contains(v);

            var pieceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var piezas = spec["piezas"] as JArray;
            if (piezas != null)
                for (int i = 0; i < piezas.Count; i++)
                {
                    var p = piezas[i] as JObject;
                    var path = $"piezas[{i}]";
                    if (p == null) { Err(path, "debe ser un objeto"); continue; }
                    var n = Text(p["nombre"]);
                    if (string.IsNullOrWhiteSpace(n)) Err(path + ".nombre", "es obligatorio");
                    else if (!pieceNames.Add(n)) Err(path + ".nombre", $"'{n}' está repetido");
                    var tipo = (Text(p["tipo"]) ?? "").ToLowerInvariant();
                    if (!PieceTypes.Contains(tipo)) { Err(path + ".tipo", $"debe ser {string.Join(", ", PieceTypes)}"); continue; }

                    if (tipo == "extrusion")
                    {
                        var perfil = p["perfil"] as JObject;
                        if (perfil == null) Err(path + ".perfil", "es obligatorio (objeto con 'forma')");
                        else
                        {
                            var forma = Text(perfil["forma"]);
                            if (!ProfileFactory.Shapes.Any(sh => string.Equals(sh, forma, StringComparison.OrdinalIgnoreCase)))
                                Err(path + ".perfil.forma", $"debe ser {string.Join(", ", ProfileFactory.Shapes)}");
                            foreach (var prop in perfil.Properties().Where(x => x.Name != "forma"))
                            {
                                if (prop.Name == "puntos")
                                {
                                    if (!(prop.Value is JArray pts) || pts.Count < 3 || pts.Any(x => !(x is JArray pa) || pa.Count != 2))
                                        Err($"{path}.perfil.puntos", "debe ser una lista de al menos 3 pares [u, v]");
                                    continue;
                                }
                                if (prop.Name == "centro")
                                {
                                    if (!(prop.Value is JArray c) || c.Count != 2) Err($"{path}.perfil.centro", "debe ser [u, v]");
                                    else
                                        for (int k = 0; k < 2; k++)
                                        {
                                            var cv = Text(c[k]);
                                            if (!string.IsNullOrWhiteSpace(cv) && !IsKnownParam(cv))
                                                Err($"{path}.perfil.centro[{k}]", $"'{cv}' no es un número ni un parámetro declarado en 'parametros'");
                                        }
                                    continue;
                                }
                                var v = Text(prop.Value);
                                if (string.IsNullOrWhiteSpace(v)) Err($"{path}.perfil.{prop.Name}", "no puede estar vacío");
                                else if (!IsKnownParam(v) && prop.Name != "diametro_int")
                                    Err($"{path}.perfil.{prop.Name}", $"'{v}' no es un número ni un parámetro declarado en 'parametros'");
                            }
                        }
                        try { FamilyGeo.ParseAxis(Text(p["eje"]), "eje"); } catch (ToolException ex) { Err(path + ".eje", ex.Message); }
                        foreach (var key in new[] { "desde", "hasta" })
                        {
                            var v = Text(p[key]);
                            if (string.IsNullOrWhiteSpace(v)) Err($"{path}.{key}", "es obligatorio");
                            else if (!IsKnownParam(v)) Err($"{path}.{key}", $"'{v}' no es un número ni un parámetro declarado en 'parametros'");
                        }
                    }
                    else if (tipo == "revolucion")
                    {
                        foreach (var key in new[] { "plano", "eje", "angulo_inicio", "angulo_fin" })
                            if (p[key] == null) Err($"{path}.{key}", "es obligatorio");
                        if (p["perfil_puntos"] == null && p["circulo"] == null) Err(path, "indica 'perfil_puntos' o 'circulo'");
                    }
                    else if (tipo == "barrido")
                    {
                        foreach (var key in new[] { "camino", "perfil" })
                            if (p[key] == null) Err($"{path}.{key}", "es obligatorio");
                    }
                    else if (p["familia"] == null && p["anidada_doc_id"] == null) Err(path, "una pieza 'anidada' necesita 'familia' (nombre o ruta .rfa)");
                }

            var typeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tipos = spec["tipos"] as JArray;
            if (tipos != null)
                for (int i = 0; i < tipos.Count; i++)
                {
                    var t = tipos[i] as JObject;
                    var path = $"tipos[{i}]";
                    if (t == null) { Err(path, "debe ser un objeto"); continue; }
                    var n = Text(t["nombre"]);
                    if (string.IsNullOrWhiteSpace(n)) Err(path + ".nombre", "es obligatorio");
                    else if (!typeNames.Add(n)) Err(path + ".nombre", $"'{n}' está repetido");
                    if (t["valores"] is JObject vals)
                        foreach (var prop in vals.Properties())
                            if (!paramNames.Contains(prop.Name)) Err($"{path}.valores.{prop.Name}", "no es un parámetro declarado en 'parametros'");
                }

            var save = Text(spec["guardar_en"]);
            if (!string.IsNullOrEmpty(save) && (!Path.IsPathRooted(save) || !save.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)))
                Err("guardar_en", "debe ser una ruta absoluta terminada en .rfa");

            return errors;
        }

        // ── Construcción ───────────────────────────────────────────────────────
        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var spec = args.RequireObj("spec");
            var errors = Validate(spec);
            if (args.Bool("validar_solamente", false))
                return new { valido = errors.Count == 0, errores = errors };
            if (errors.Count > 0)
                throw new ToolException($"El spec tiene {errors.Count} error(es), no se creó nada:\n- " + string.Join("\n- ", errors));

            var nombre = Text(spec["nombre"]);
            var steps = new List<object>();
            FamilySession session = null;
            string step = "crear familia";

            try
            {
                new CreateFamilyTool().Run(new ToolArgs(new JObject
                {
                    ["nombre"] = nombre,
                    ["categoria"] = Text(spec["categoria"]) ?? "modelo_generico",
                    ["plantilla_rft"] = Text(spec["plantilla"]) ?? ""
                }), ctx);
                session = FamilySessions.All().Last();
                var id = session.Id;
                JObject With(JObject o) { o["familia_doc_id"] = id; return o; }

                var tipos = spec["tipos"] as JArray ?? new JArray();
                var firstType = tipos.Count > 0 ? Text(tipos[0]["nombre"]) : null;
                if (firstType != null && !firstType.Equals(session.Doc.FamilyManager.CurrentType?.Name, StringComparison.OrdinalIgnoreCase))
                    ToolHelpers.InTransaction(session.Doc, "Renombrar tipo inicial", () => { session.Doc.FamilyManager.RenameCurrentType(firstType); return 0; });
                var currentTypeName = session.Doc.FamilyManager.CurrentType.Name;

                // Parámetros y valores del primer tipo
                var parametros = spec["parametros"] as JArray ?? new JArray();
                foreach (JObject p in parametros)
                {
                    step = $"parámetro '{Text(p["nombre"])}'";
                    new AddFamilyParameterTool().Run(new ToolArgs(With(new JObject
                    {
                        ["nombre"] = Text(p["nombre"]),
                        ["tipo_dato"] = Text(p["tipo"]),
                        ["grupo"] = Text(p["grupo"]) ?? "Geometria",
                        ["es_instancia"] = p["instancia"] ?? false
                    })), ctx);
                }

                var firstValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (JObject p in parametros) if (p["valor"] != null) firstValues[Text(p["nombre"])] = Text(p["valor"]);
                if (tipos.Count > 0 && tipos[0]["valores"] is JObject v0)
                    foreach (var prop in v0.Properties()) firstValues[prop.Name] = Text(prop.Value);
                foreach (var kv in firstValues)
                {
                    step = $"valor de '{kv.Key}' en '{currentTypeName}'";
                    new SetFamilyTypeValueTool().Run(new ToolArgs(With(new JObject { ["tipo"] = currentTypeName, ["parametro"] = kv.Key, ["valor"] = kv.Value })), ctx);
                }

                foreach (JObject p in parametros.Where(x => x["formula"] != null))
                {
                    step = $"fórmula de '{Text(p["nombre"])}'";
                    new SetFamilyFormulaTool().Run(new ToolArgs(With(new JObject { ["parametro"] = Text(p["nombre"]), ["formula"] = Text(p["formula"]) })), ctx);
                }

                // Planos de referencia propios
                foreach (JObject pl in spec["planos_referencia"] as JArray ?? new JArray())
                {
                    step = $"plano '{Text(pl["nombre"])}'";
                    new CreateReferencePlaneTool().Run(new ToolArgs(With(Clone(pl))), ctx);
                }

                // Piezas
                foreach (JObject piece in spec["piezas"] as JArray ?? new JArray())
                {
                    var tipo = Text(piece["tipo"]).ToLowerInvariant();
                    step = $"pieza '{Text(piece["nombre"])}' ({tipo})";
                    var o = With(Clone(piece, "tipo"));
                    if (o["es_vacio"] == null && piece["vacio"] != null) o["es_vacio"] = piece["vacio"];
                    o.Remove("vacio");
                    McpTool tool = tipo == "extrusion" ? (McpTool)new CreateExtrusionTool()
                        : tipo == "revolucion" ? new CreateRevolutionTool()
                        : tipo == "barrido" ? new CreateSweepTool()
                        : new NestFamilyTool();
                    var result = tool.Run(new ToolArgs(o), ctx);
                    steps.Add(new { pieza = Text(piece["nombre"]), tipo, resultado = result });

                    if (tipo == "anidada" && piece["asociar"] is JArray assoc)
                        foreach (JObject a in assoc)
                        {
                            step = $"asociación {Text(a["parametro_anidado"])} → {Text(a["parametro_host"])}";
                            var ao = With(Clone(a));
                            ao["instancia"] = Text(piece["nombre"]);
                            new AssociateNestedParameterTool().Run(new ToolArgs(ao), ctx);
                        }
                }

                // Tipos restantes
                for (int i = 1; i < tipos.Count; i++)
                {
                    var t = (JObject)tipos[i];
                    step = $"tipo '{Text(t["nombre"])}'";
                    new CreateFamilyTypeTool().Run(new ToolArgs(With(new JObject { ["nombre"] = Text(t["nombre"]) })), ctx);
                    if (t["valores"] is JObject vals)
                        foreach (var prop in vals.Properties())
                        {
                            step = $"valor de '{prop.Name}' en '{Text(t["nombre"])}'";
                            new SetFamilyTypeValueTool().Run(new ToolArgs(With(new JObject { ["tipo"] = Text(t["nombre"]), ["parametro"] = prop.Name, ["valor"] = Text(prop.Value) })), ctx);
                        }
                }
                if (tipos.Count > 1)
                    new ActivateFamilyTypeTool().Run(new ToolArgs(With(new JObject { ["tipo"] = firstType })), ctx);

                // Guardado
                string archivo = null;
                var save = Text(spec["guardar_en"]);
                if (!string.IsNullOrEmpty(save))
                {
                    step = "guardar";
                    new SaveFamilyTool().Run(new ToolArgs(With(new JObject { ["ruta"] = save, ["sobrescribir"] = spec["sobrescribir"] ?? false })), ctx);
                    archivo = save;
                }

                var warnings = new List<string>();
                return new
                {
                    familia_doc_id = id,
                    nombre,
                    archivo,
                    tipos = session.Doc.FamilyManager.Types.Cast<FamilyType>().Select(t => t.Name),
                    piezas = steps,
                    siguiente_paso = archivo == null
                        ? "La familia está abierta: guárdala con save_family o cárgala en el proyecto con load_family (familia_doc_id)"
                        : "Cárgala en el proyecto con load_family (ruta o familia_doc_id) y colócala con place_family_instance"
                };
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                if (session != null)
                {
                    if (args.Bool("conservar_si_falla", false))
                        throw new ToolException($"Falló en el paso «{step}»: {ex.Message}. La familia incompleta quedó abierta (familia_doc_id {session.Id}) para inspeccionarla");
                    try { session.Doc.Close(false); } catch { }
                    FamilySessions.Remove(session.Id);
                }
                throw new ToolException($"Falló en el paso «{step}»: {ex.Message}. La familia incompleta se descartó; corrige el spec y repite");
            }
        }
    }
}
