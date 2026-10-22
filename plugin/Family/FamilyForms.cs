#nullable disable
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitMcpPlugin.Families
{
    /// <summary>Creación de piezas (extrusión, barrido, revolución) dentro del documento de familia. Requiere transacción abierta.</summary>
    internal static class FamilyForms
    {
        private static string Clean(string name)
        {
            var chars = name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
            return new string(chars);
        }

        /// <summary>
        /// Revit prohíbe cambiar el Name de una pieza (GenericForm.Name lanza InvalidOperationException), así que los nombres
        /// de pieza viven solo en el registro de la sesión. Los planos auxiliares sí se nombran, con un prefijo que nunca
        /// colisiona con planos existentes (ni con los de otras piezas), sin importar qué nombre pase el usuario.
        /// </summary>
        private static string UniquePrefix(Document d, string name)
        {
            var existing = new FilteredElementCollector(d).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                .Select(p => p.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
            var baseName = Clean(name);
            var candidate = baseName;
            int n = 1;
            while (existing.Any(x => x.StartsWith(candidate + "_", StringComparison.OrdinalIgnoreCase) ||
                                     x.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                candidate = baseName + "_" + (++n);
            return candidate;
        }

        private static DimArg Offset(Document d, string text, string field)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ToolException($"'{field}' es obligatorio (valor en metros o nombre de un parámetro de longitud)");
            if (FamilyValues.IsNumeric(text)) return new DimArg { Feet = FamilyValues.LengthFeet(text) };
            var fp = FamilyOps.RequireParam(d.FamilyManager, text);
            if (FamilyValues.KindOf(fp) != ParamKind.Length) throw new ToolException($"'{field}': el parámetro '{text}' no es de tipo Longitud");
            return new DimArg { Param = text, Feet = FamilyOps.CurrentDouble(d, fp) };
        }

        private static bool Align(Document d, View view, Reference plane, Reference other, string what, List<string> warnings, string planeName)
        {
            try
            {
                var dim = d.FamilyCreate.NewAlignment(view, plane, other);
                try { dim.IsLocked = true; } catch { }
                return true;
            }
            catch (Exception ex)
            {
                warnings.Add($"No se pudo alinear {what} al plano '{planeName}' (esa parte no seguirá al parámetro): {ex.Message}");
                return false;
            }
        }

        private static bool Near(Curve c, Curve target)
        {
            if (c.GetType() != target.GetType()) return false;
            return c.Evaluate(0.5, true).DistanceTo(target.Evaluate(0.5, true)) < 1e-4 &&
                   Math.Abs(c.Length - target.Length) < 1e-4;
        }

        private static void AssociateOffset(Document d, Element ext, BuiltInParameter bip, DimArg arg, string label)
        {
            if (arg.Param == null) return;
            var elementParam = ext.get_Parameter(bip);
            if (elementParam == null) throw new ToolException($"La pieza no tiene el parámetro interno de '{label}'");
            d.FamilyManager.AssociateElementParameterToFamilyParameter(elementParam, FamilyOps.RequireParam(d.FamilyManager, arg.Param));
        }

        private static void Decorate(Document d, GenericForm form, string subcategoria, string material, List<string> warnings)
        {
            if (!string.IsNullOrWhiteSpace(subcategoria))
            {
                var parent = d.OwnerFamily.FamilyCategory;
                var sub = parent.SubCategories.Cast<Category>()
                    .FirstOrDefault(c => string.Equals(c.Name, subcategoria, StringComparison.OrdinalIgnoreCase))
                    ?? d.Settings.Categories.NewSubcategory(parent, subcategoria);
                form.Subcategory = sub;
            }
            if (!string.IsNullOrWhiteSpace(material))
            {
                var p = form.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                if (p == null) { warnings.Add("La pieza no admite material"); return; }
                var fp = d.FamilyManager.get_Parameter(material);
                if (fp != null && FamilyValues.KindOf(fp) == ParamKind.Material)
                    d.FamilyManager.AssociateElementParameterToFamilyParameter(p, fp);
                else
                    p.Set(FamilyOps.FindOrCreateMaterial(d, material));
            }
        }

        // ── Extrusión paramétrica ──────────────────────────────────────────────
        public static object CreateExtrusion(FamilySession s, string name, JObject perfil, char axis, string desde, string hasta,
                                             bool isVoid, string subcategoria, string material)
        {
            var d = s.Doc;
            var fm = d.FamilyManager;
            if (s.Forms.ContainsKey(name)) throw new ToolException($"Ya existe una pieza llamada '{name}' en esta familia");
            FamilyGeo.ProfileAxes(axis, out char U, out char V);
            var warnings = new List<string>();

            var pd = ProfileFactory.Build(d, UniquePrefix(d, name), perfil);
            var start = Offset(d, desde, "desde");
            var end = Offset(d, hasta, "hasta");
            if (end.Feet <= start.Feet + 1e-9) throw new ToolException("'hasta' debe ser mayor que 'desde'. Si es un parámetro, asígnale primero un valor positivo");

            // 1) Planos de referencia del perfil
            var planes = new Dictionary<string, ReferencePlane>();
            foreach (var pl in pd.Planes)
                planes[pl.Name] = FamilyGeo.CreatePlane(d, pl.Name, pl.Axis == 'U' ? U : V, pl.Value);
            if (pd.Dims.Any(x => x.A == "@U0")) planes["@U0"] = FamilyGeo.CenterPlane(d, U);
            if (pd.Dims.Any(x => x.A == "@V0")) planes["@V0"] = FamilyGeo.CenterPlane(d, V);
            d.Regenerate();

            // 2) Cotas etiquetadas con los parámetros (y simetría EQ respecto al centro)
            var view = FamilyGeo.ProfileView(d, axis);
            int slot = 0, labeled = 0, fixedDims = 0;
            foreach (var dd in pd.Dims)
            {
                try
                {
                    var dim = FamilyGeo.LinearDimension(d, view, new[] { planes[dd.A], planes[dd.B] }, slot++);
                    if (dd.Param != null) { dim.FamilyLabel = FamilyOps.RequireParam(fm, dd.Param); labeled++; }
                    else { try { dim.IsLocked = true; } catch { } fixedDims++; }
                }
                catch (Exception ex) { warnings.Add($"Cota {dd.A}–{dd.B}: {ex.Message}"); }
            }
            foreach (var eq in pd.Eqs)
            {
                try
                {
                    var center = eq.Center != null ? planes[eq.Center] : FamilyGeo.CenterPlane(d, eq.Axis == 'U' ? U : V);
                    var dim = FamilyGeo.LinearDimension(d, view, new[] { planes[eq.Lo], center, planes[eq.Hi] }, slot++);
                    dim.AreSegmentsEqual = true;
                }
                catch (Exception ex) { warnings.Add($"Simetría de {eq.Lo}/{eq.Hi}: {ex.Message}"); }
            }

            // 3) Geometría del perfil
            Func<double, double, XYZ> P = (u, v) => FamilyGeo.Point(axis, 0, U, u, V, v);
            var app = d.Application.Create;
            var profile = app.NewCurveArrArray();
            var edges = new List<KeyValuePair<Curve, string>>();
            foreach (var loop in pd.Loops)
            {
                var ca = app.NewCurveArray();
                for (int i = 0; i < loop.Vertices.Count; i++)
                {
                    var a = loop.Vertices[i];
                    var b = loop.Vertices[(i + 1) % loop.Vertices.Count];
                    var pa = P(pd.Value(a[0]), pd.Value(a[1]));
                    var pb = P(pd.Value(b[0]), pd.Value(b[1]));
                    if (pa.DistanceTo(pb) < 1e-9) continue;
                    var line = Line.CreateBound(pa, pb);
                    ca.Append(line);
                    edges.Add(new KeyValuePair<Curve, string>(line, a[0] == b[0] ? a[0] : a[1]));
                }
                profile.Append(ca);
            }
            foreach (var fixedLoop in pd.FixedLoops)
            {
                var ca = app.NewCurveArray();
                for (int i = 0; i < fixedLoop.Count; i++)
                {
                    var a = P(fixedLoop[i][0], fixedLoop[i][1]);
                    var b = P(fixedLoop[(i + 1) % fixedLoop.Count][0], fixedLoop[(i + 1) % fixedLoop.Count][1]);
                    if (a.DistanceTo(b) > 1e-9) ca.Append(Line.CreateBound(a, b));
                }
                profile.Append(ca);
            }
            var circles = new List<Arc>();
            foreach (var c in pd.Circles)
            {
                var arc = Arc.Create(P(c.CU, c.CV), c.Diameter / 2, 0, 2 * Math.PI, FamilyGeo.AxisVector(U), FamilyGeo.AxisVector(V));
                var ca = app.NewCurveArray();
                ca.Append(arc);
                profile.Append(ca);
                circles.Add(arc);
            }

            var sketchPlane = SketchPlane.Create(d, Plane.CreateByNormalAndOrigin(FamilyGeo.AxisVector(axis), XYZ.Zero));
            var ext = d.FamilyCreate.NewExtrusion(!isVoid, profile, sketchPlane, end.Feet);
            if (start.Feet > 1e-9) ext.StartOffset = start.Feet;
            ext.EndOffset = end.Feet;
            AssociateOffset(d, ext, BuiltInParameter.EXTRUSION_START_PARAM, start, "desde");
            AssociateOffset(d, ext, BuiltInParameter.EXTRUSION_END_PARAM, end, "hasta");
            d.Regenerate();

            // 4) Alineaciones bloqueadas entre el perfil y los planos
            var sketchCurves = ext.Sketch.GetDependentElements(new ElementClassFilter(typeof(CurveElement))).Select(id => d.GetElement(id)).OfType<ModelCurve>().ToList();
            int aligned = 0, expected = edges.Count;
            foreach (var e in edges)
            {
                var mc = sketchCurves.FirstOrDefault(m => Near(m.GeometryCurve, e.Key));
                if (mc == null) { warnings.Add($"No se encontró la arista del perfil junto a '{e.Value}'"); continue; }
                if (Align(d, view, planes[e.Value].GetReference(), mc.GeometryCurve.Reference, "una arista del perfil", warnings, e.Value)) aligned++;
            }

            for (int i = 0; i < circles.Count; i++)
            {
                var arc = circles[i];
                var mc = sketchCurves.FirstOrDefault(m => m.GeometryCurve is Arc a && Math.Abs(a.Radius - arc.Radius) < 1e-4);
                if (mc == null) { warnings.Add("No se encontró el círculo del perfil"); continue; }
                expected += 3;
                var cdef = pd.Circles[i];
                var cu = cdef.CenterU != null ? planes[cdef.CenterU] : FamilyGeo.CenterPlane(d, U);
                var cv = cdef.CenterV != null ? planes[cdef.CenterV] : FamilyGeo.CenterPlane(d, V);
                if (Align(d, view, cu.GetReference(), mc.CenterPointReference, "el centro del círculo", warnings, cu.Name)) aligned++;
                if (Align(d, view, cv.GetReference(), mc.CenterPointReference, "el centro del círculo", warnings, cv.Name)) aligned++;
                try
                {
                    var dim = d.FamilyCreate.NewDiameterDimension(view, mc.GeometryCurve.Reference, P(pd.Circles[i].CU + arc.Radius * 0.7, pd.Circles[i].CV + arc.Radius * 0.7));
                    var cd = pd.Circles[i];
                    if (cd.Param != null) { dim.FamilyLabel = FamilyOps.RequireParam(fm, cd.Param); labeled++; }
                    else { try { dim.IsLocked = true; } catch { } fixedDims++; }
                    aligned++;
                }
                catch (Exception ex) { warnings.Add($"Cota de diámetro: {ex.Message} (el tamaño del círculo no seguirá al parámetro)"); }
            }

            Decorate(d, ext, subcategoria, material, warnings);
            try { d.Regenerate(); }
            catch (Exception ex) { warnings.Add($"La regeneración final dio un aviso: {ex.Message}"); }

            s.Forms[name] = new FormInfo { Id = ext.Id, Axis = axis, Kind = "extrusion" };
            return new
            {
                pieza = name,
                id = ext.Id.GetId(),
                eje = axis.ToString(),
                es_vacio = isVoid,
                planos_creados = planes.Keys,
                cotas_con_parametro = labeled,
                cotas_fijas = fixedDims,
                alineaciones_ok = aligned,
                alineaciones_esperadas = expected,
                parametrico = warnings.Count == 0,
                advertencias = warnings
            };
        }

        // ── Revolución ─────────────────────────────────────────────────────────
        public static object CreateRevolution(FamilySession s, string name, string plano, JToken perfilPuntos, JObject circulo, JToken eje,
                                              string anguloInicio, string anguloFin, bool isVoid, string subcategoria, string material)
        {
            var d = s.Doc;
            if (s.Forms.ContainsKey(name)) throw new ToolException($"Ya existe una pieza llamada '{name}' en esta familia");
            var pl = (plano ?? "").Trim().ToUpperInvariant();
            if (pl.Length != 2 || pl[0] == pl[1] || pl.Any(c => "XYZ".IndexOf(c) < 0))
                throw new ToolException("'plano' debe ser XY, XZ o YZ (los dos ejes del plano del perfil)");
            char U = pl[0], V = pl[1];
            char normal = "XYZ".First(c => c != U && c != V);
            Func<double, double, XYZ> P = (u, v) => FamilyGeo.Point(normal, 0, U, u / Units.FeetToMeters, V, v / Units.FeetToMeters);

            var app = d.Application.Create;
            var profile = app.NewCurveArrArray();
            var ca = app.NewCurveArray();
            if (circulo != null)
            {
                var c = circulo["centro"] as JArray;
                var dia = FamilyValues.LengthFeet(Convert.ToString((circulo["diametro"] as JValue)?.Value, CultureInfo.InvariantCulture));
                var center = P(c == null ? 0 : (double)c[0], c == null ? 0 : (double)c[1]);
                ca.Append(Arc.Create(center, dia / 2, 0, 2 * Math.PI, FamilyGeo.AxisVector(U), FamilyGeo.AxisVector(V)));
            }
            else
            {
                var pts = (perfilPuntos as JArray)?.Select(p => new[] { (double)p[0], (double)p[1] }).ToList();
                if (pts == null || pts.Count < 3) throw new ToolException("Indica 'perfil_puntos' (mínimo 3 puntos [a,b] en metros) o 'circulo'");
                for (int i = 0; i < pts.Count; i++)
                {
                    var a = P(pts[i][0], pts[i][1]);
                    var b = P(pts[(i + 1) % pts.Count][0], pts[(i + 1) % pts.Count][1]);
                    if (a.DistanceTo(b) > 1e-9) ca.Append(Line.CreateBound(a, b));
                }
            }
            profile.Append(ca);

            var ax = eje as JArray;
            if (ax == null || ax.Count != 2) throw new ToolException("'eje' debe ser dos puntos [[a1,b1],[a2,b2]] en el plano del perfil (metros)");
            var axisLine = Line.CreateBound(P((double)ax[0][0], (double)ax[0][1]), P((double)ax[1][0], (double)ax[1][1]));

            var fm = d.FamilyManager;
            double a0 = AngleArg(d, anguloInicio, "angulo_inicio", out string startParam);
            double a1 = AngleArg(d, anguloFin, "angulo_fin", out string endParam);
            if (a1 <= a0) throw new ToolException("'angulo_fin' debe ser mayor que 'angulo_inicio'");

            var sp = SketchPlane.Create(d, Plane.CreateByNormalAndOrigin(FamilyGeo.AxisVector(normal), XYZ.Zero));
            var rev = d.FamilyCreate.NewRevolution(!isVoid, profile, sp, axisLine, a0, a1);
            var warnings = new List<string>();
            if (startParam != null) fm.AssociateElementParameterToFamilyParameter(rev.get_Parameter(BuiltInParameter.REVOLUTION_START_ANGLE), fm.get_Parameter(startParam));
            if (endParam != null) fm.AssociateElementParameterToFamilyParameter(rev.get_Parameter(BuiltInParameter.REVOLUTION_END_ANGLE), fm.get_Parameter(endParam));
            Decorate(d, rev, subcategoria, material, warnings);
            d.Regenerate();

            s.Forms[name] = new FormInfo { Id = rev.Id, Axis = normal, Kind = "revolution" };
            warnings.Add("El perfil y el eje de la revolución son fijos; solo los ángulos pueden seguir a un parámetro");
            return new { pieza = name, id = rev.Id.GetId(), plano = pl, es_vacio = isVoid, advertencias = warnings };
        }

        private static double AngleArg(Document d, string text, string field, out string param)
        {
            param = null;
            if (string.IsNullOrWhiteSpace(text)) throw new ToolException($"'{field}' es obligatorio (grados, o nombre de un parámetro de ángulo)");
            if (FamilyValues.IsNumeric(text)) return FamilyValues.AngleRadians(text);
            var fp = FamilyOps.RequireParam(d.FamilyManager, text);
            if (FamilyValues.KindOf(fp) != ParamKind.Angle) throw new ToolException($"'{field}': el parámetro '{text}' no es de tipo Angulo");
            param = text;
            return FamilyOps.CurrentDouble(d, fp);
        }

        // ── Barrido ──────────────────────────────────────────────────────────────
        /// <summary>
        /// NewSweep solo acepta (comprobado en Revit 2025) el perfil en el plano XY y el camino saliendo del origen hacia +Z:
        /// con caminos horizontales o inclinados lanza ArgumentException. Por eso el barrido se construye en ese marco local y
        /// después se gira (de +Z a la dirección real del camino) y se mueve a su posición.
        /// </summary>
        public static object CreateSweep(FamilySession s, string name, JToken camino, JObject perfil, bool isVoid, string subcategoria,
                                         string material, double giroPerfilGrados = 0)
        {
            var d = s.Doc;
            if (s.Forms.ContainsKey(name)) throw new ToolException($"Ya existe una pieza llamada '{name}' en esta familia");
            var pts = (camino as JArray)?.Select(p => new XYZ((double)p[0], (double)p[1], (double)p[2]) / Units.FeetToMeters).ToList();
            if (pts == null || pts.Count < 2) throw new ToolException("'camino' necesita al menos 2 puntos [x,y,z] en metros");
            if (pts[0].DistanceTo(pts[1]) < 1e-9) throw new ToolException("Los dos primeros puntos del 'camino' coinciden");

            // Rotación R que lleva +Z a la dirección inicial del camino
            var t = (pts[1] - pts[0]).Normalize();
            var cross = XYZ.BasisZ.CrossProduct(t);
            XYZ axis;
            double angle;
            if (cross.GetLength() < 1e-9) { axis = XYZ.BasisX; angle = t.Z > 0 ? 0 : Math.PI; }
            else { axis = cross.Normalize(); angle = Math.Acos(Math.Max(-1, Math.Min(1, XYZ.BasisZ.DotProduct(t)))); }
            var inverse = Math.Abs(angle) < 1e-12 ? Transform.Identity : Transform.CreateRotationAtPoint(axis, angle, XYZ.Zero).Inverse;
            var local = pts.Select(p => inverse.OfVector(p - pts[0])).ToList();

            // Plano del camino en el marco local (contiene a +Z)
            XYZ n = null;
            for (int i = 2; i < local.Count && n == null; i++)
            {
                var c = XYZ.BasisZ.CrossProduct(local[i].Normalize());
                if (c.GetLength() > 1e-6) n = c.Normalize();
            }
            if (n == null) n = XYZ.BasisY;
            foreach (var p in local)
                if (Math.Abs(p.DotProduct(n)) > 1e-4) throw new ToolException("Los puntos del 'camino' deben estar en un mismo plano");

            var app = d.Application.Create;
            var path = app.NewCurveArray();
            for (int i = 0; i < local.Count - 1; i++) path.Append(Line.CreateBound(local[i], local[i + 1]));

            var forma = ((string)perfil?["forma"] ?? "").Trim().ToLowerInvariant();
            double L(string key) => FamilyValues.LengthFeet(Convert.ToString((perfil[key] as JValue)?.Value, CultureInfo.InvariantCulture));
            var loop = app.NewCurveArray();
            if (forma == "circulo")
                loop.Append(Arc.Create(XYZ.Zero, L("diametro") / 2, 0, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
            else if (forma == "rectangulo")
            {
                double w = L("ancho") / 2, h = L("alto") / 2;
                var c = new[] { new XYZ(-w, -h, 0), new XYZ(w, -h, 0), new XYZ(w, h, 0), new XYZ(-w, h, 0) };
                for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(c[i], c[(i + 1) % 4]));
            }
            else throw new ToolException("perfil.forma del barrido debe ser circulo{diametro} o rectangulo{ancho,alto} (valores fijos en metros). " +
                                         "'ancho' va en el plano del camino... y 'alto' perpendicular a él");
            var loops = app.NewCurveArrArray();
            loops.Append(loop);

            var sp = SketchPlane.Create(d, Plane.CreateByNormalAndOrigin(n, XYZ.Zero));
            var sweep = d.FamilyCreate.NewSweep(!isVoid, path, sp, app.NewCurveLoopsProfile(loops), 0, ProfilePlaneLocation.Start);

            // Colocarlo: girar +Z hacia el camino real, girar el perfil sobre el camino (opcional) y mover al punto inicial
            if (Math.Abs(angle) > 1e-12) ElementTransformUtils.RotateElement(d, sweep.Id, Line.CreateBound(XYZ.Zero, axis), angle);
            if (Math.Abs(giroPerfilGrados) > 1e-9)
                ElementTransformUtils.RotateElement(d, sweep.Id, Line.CreateBound(XYZ.Zero, t), giroPerfilGrados * Math.PI / 180.0);
            if (pts[0].GetLength() > 1e-9) ElementTransformUtils.MoveElement(d, sweep.Id, pts[0]);

            var warnings = new List<string> { "Las dimensiones del barrido son fijas (no siguen parámetros de la familia)" };
            Decorate(d, sweep, subcategoria, material, warnings);
            d.Regenerate();

            s.Forms[name] = new FormInfo { Id = sweep.Id, Kind = "sweep" };
            return new { pieza = name, id = sweep.Id.GetId(), es_vacio = isVoid, advertencias = warnings };
        }
    }
}
