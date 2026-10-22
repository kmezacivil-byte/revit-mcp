#nullable disable
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitMcpPlugin.Families
{
    /// <summary>Valor de una dimensión de perfil: nombre de un parámetro de la familia, o una medida fija.</summary>
    internal sealed class DimArg
    {
        public string Param;    // null = medida fija (la cota no se etiqueta)
        public double Feet;
    }

    internal sealed class PlaneDef { public string Name; public char Axis; public double Value; }   // Axis: 'U' o 'V'
    internal sealed class DimDef { public string A, B, Param; }
    internal sealed class EqDef { public string Lo, Hi; public char Axis; public string Center; }   // Center null = plano central del origen
    internal sealed class LoopDef { public List<string[]> Vertices = new List<string[]>(); }       // [plano U, plano V]
    internal sealed class CircleDef { public double Diameter; public string Param; public double CU, CV; public string CenterU, CenterV; }

    /// <summary>Un perfil descrito por planos de referencia, cotas con etiqueta y contornos que se alinean a esos planos.</summary>
    internal sealed class ProfileDef
    {
        public List<PlaneDef> Planes = new List<PlaneDef>();
        public List<DimDef> Dims = new List<DimDef>();
        public List<EqDef> Eqs = new List<EqDef>();
        public List<LoopDef> Loops = new List<LoopDef>();
        public List<CircleDef> Circles = new List<CircleDef>();
        public List<List<double[]>> FixedLoops = new List<List<double[]>>();   // contornos fijos [u,v] en pies (polígonos)
        public double Value(string plane) => Planes.First(p => p.Name == plane).Value;
    }

    internal static class ProfileFactory
    {
        public static readonly string[] Shapes = { "rectangulo", "circulo", "anillo", "cajon", "perfil_I", "perfil_C", "perfil_L", "poligono" };

        public static string Help =>
            "rectangulo{ancho,alto}, circulo{diametro}, anillo{diametro_ext,espesor[,diametro_int]}, cajon{ancho,alto,espesor}, " +
            "perfil_I{h,b,tw,tf}, perfil_C{h,b,tw,tf}, perfil_L{h,b,t}, poligono{puntos:[[u,v],...]} (contorno fijo, p.ej. una diagonal a 45° o una cartela triangular). " +
            "Cada medida es el nombre de un parámetro de longitud de la familia (queda parametrizada) o un valor fijo (metros, o con unidad \"125 mm\"). " +
            "centro:[u,v] desplaza el perfil: cada valor es un número fijo o un parámetro de longitud (distancia positiva desde el origen), " +
            "y así la posición de la pieza sigue al parámetro.";

        private static DimArg Dim(Document d, JObject spec, string shape, string key)
        {
            var tok = spec[key];
            if (tok == null) throw new ToolException($"perfil.{key} es obligatorio para la forma '{shape}'");
            var text = Convert.ToString((tok as JValue)?.Value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text)) throw new ToolException($"perfil.{key} debe ser un parámetro o un valor");
            if (FamilyValues.IsNumeric(text)) return new DimArg { Feet = FamilyValues.LengthFeet(text) };

            var fp = FamilyOps.RequireParam(d.FamilyManager, text);
            if (FamilyValues.KindOf(fp) != ParamKind.Length)
                throw new ToolException($"perfil.{key}: el parámetro '{text}' no es de tipo Longitud");
            var value = FamilyOps.CurrentDouble(d, fp);
            if (value <= 1e-9)
                throw new ToolException($"perfil.{key}: el parámetro '{text}' vale 0. Asígnale un valor con set_family_type_value antes de dibujar el perfil");
            return new DimArg { Param = text, Feet = value };
        }

        /// <summary>Parámetro auxiliar calculado por fórmula (p.ej. diámetro interior = exterior - 2 * espesor).</summary>
        private static DimArg Derived(Document d, string name, DimArg main, DimArg thickness, double feet)
        {
            if (main.Param == null || thickness.Param == null) return new DimArg { Feet = feet };
            var fm = d.FamilyManager;
            if (fm.get_Parameter(name) == null)
            {
                var fp = FamilyOps.AddParameter(d, name, "Longitud", "Geometria", false);
                fm.SetFormula(fp, $"{main.Param} - 2 * {thickness.Param}");
            }
            return new DimArg { Param = name, Feet = feet };
        }

        private static void Sym(ProfileDef pd, string prefix, char axis, string role, DimArg size, out string lo, out string hi)
        {
            lo = $"{prefix}_{role}-";
            hi = $"{prefix}_{role}+";
            pd.Planes.Add(new PlaneDef { Name = lo, Axis = axis, Value = -size.Feet / 2 });
            pd.Planes.Add(new PlaneDef { Name = hi, Axis = axis, Value = size.Feet / 2 });
            pd.Dims.Add(new DimDef { A = lo, B = hi, Param = size.Param });
            pd.Eqs.Add(new EqDef { Lo = lo, Hi = hi, Axis = axis });
        }

        private static void Plane(ProfileDef pd, string name, char axis, double value) =>
            pd.Planes.Add(new PlaneDef { Name = name, Axis = axis, Value = value });

        private static void Rect(ProfileDef pd, string prefix, string tag, DimArg w, DimArg h)
        {
            Sym(pd, prefix, 'U', tag + "U", w, out var un, out var up);
            Sym(pd, prefix, 'V', tag + "V", h, out var vn, out var vp);
            var l = new LoopDef();
            l.Vertices.Add(new[] { un, vn }); l.Vertices.Add(new[] { up, vn });
            l.Vertices.Add(new[] { up, vp }); l.Vertices.Add(new[] { un, vp });
            pd.Loops.Add(l);
        }

        /// <summary>Valor con signo: número fijo o parámetro de longitud (se admite 0 y negativos en valores fijos).</summary>
        private static DimArg Signed(Document d, JToken tok)
        {
            var text = Convert.ToString((tok as JValue)?.Value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text)) return new DimArg { Feet = 0 };
            if (FamilyValues.IsNumeric(text)) return new DimArg { Feet = FamilyValues.LengthFeet(text) };
            var fp = FamilyOps.RequireParam(d.FamilyManager, text);
            if (FamilyValues.KindOf(fp) != ParamKind.Length) throw new ToolException($"perfil.centro: el parámetro '{text}' no es de tipo Longitud");
            return new DimArg { Param = text, Feet = FamilyOps.CurrentDouble(d, fp) };
        }

        /// <summary>
        /// Desplaza todo el perfil 'arg' pies sobre el eje U o V. Con un parámetro, crea un plano central propio y una cota
        /// etiquetada entre el plano central del origen y ese plano: así la posición del perfil sigue al parámetro.
        /// </summary>
        private static void ApplyCenter(ProfileDef pd, string prefix, char axis, DimArg arg)
        {
            if (arg.Param == null && Math.Abs(arg.Feet) < 1e-9) return;
            if (pd.FixedLoops.Count > 0 && arg.Param != null)
                throw new ToolException("El perfil 'poligono' es fijo: su 'centro' debe ser un valor, no un parámetro");

            foreach (var p in pd.Planes.Where(x => x.Axis == axis)) p.Value += arg.Feet;
            foreach (var loop in pd.FixedLoops)
                foreach (var pt in loop) pt[axis == 'U' ? 0 : 1] += arg.Feet;

            string cname = prefix + "_C" + axis;
            if (pd.Loops.Count > 0 || pd.Circles.Count > 0)
            {
                pd.Planes.Add(new PlaneDef { Name = cname, Axis = axis, Value = arg.Feet });
                pd.Dims.Add(new DimDef { A = "@" + axis + "0", B = cname, Param = arg.Param });
                foreach (var eq in pd.Eqs.Where(x => x.Axis == axis)) eq.Center = cname;
            }
            foreach (var c in pd.Circles)
            {
                if (axis == 'U') { c.CU = arg.Feet; c.CenterU = cname; }
                else { c.CV = arg.Feet; c.CenterV = cname; }
            }
        }

        public static ProfileDef Build(Document d, string prefix, JObject spec)
        {
            var shape = (string)spec["forma"];
            if (string.IsNullOrWhiteSpace(shape)) throw new ToolException($"perfil.forma es obligatoria. Formas: {string.Join(", ", Shapes)}");
            var canon = Shapes.FirstOrDefault(s => string.Equals(s, shape.Trim(), StringComparison.OrdinalIgnoreCase));
            if (canon == null) throw new ToolException($"Forma '{shape}' no soportada. Formas: {string.Join(", ", Shapes)}");

            var pd = new ProfileDef();
            switch (canon)
            {
                case "poligono":
                {
                    var pts = spec["puntos"] as JArray;
                    if (pts == null || pts.Count < 3) throw new ToolException("perfil.puntos necesita al menos 3 puntos [u,v] (metros o con unidad)");
                    var loop = new List<double[]>();
                    foreach (var p in pts)
                    {
                        var pa = p as JArray;
                        if (pa == null || pa.Count != 2) throw new ToolException("perfil.puntos debe ser una lista de pares [u, v]");
                        loop.Add(new[] { FamilyValues.LengthFeet(Convert.ToString((pa[0] as JValue)?.Value, CultureInfo.InvariantCulture)),
                                         FamilyValues.LengthFeet(Convert.ToString((pa[1] as JValue)?.Value, CultureInfo.InvariantCulture)) });
                    }
                    pd.FixedLoops.Add(loop);
                    break;
                }

                case "rectangulo":
                    Rect(pd, prefix, "", Dim(d, spec, canon, "ancho"), Dim(d, spec, canon, "alto"));
                    break;

                case "circulo":
                {
                    var dia = Dim(d, spec, canon, "diametro");
                    pd.Circles.Add(new CircleDef { Diameter = dia.Feet, Param = dia.Param });
                    break;
                }

                case "anillo":
                {
                    var ext = Dim(d, spec, canon, "diametro_ext");
                    var esp = Dim(d, spec, canon, "espesor");
                    if (esp.Feet * 2 >= ext.Feet) throw new ToolException("perfil.espesor debe ser menor que la mitad del diámetro exterior");
                    DimArg inner;
                    if (spec["diametro_int"] != null) inner = Dim(d, spec, canon, "diametro_int");
                    else inner = Derived(d, (ext.Param ?? prefix) + "_Int", ext, esp, ext.Feet - 2 * esp.Feet);
                    pd.Circles.Add(new CircleDef { Diameter = ext.Feet, Param = ext.Param });
                    pd.Circles.Add(new CircleDef { Diameter = inner.Feet, Param = inner.Param });
                    break;
                }

                case "cajon":
                {
                    var w = Dim(d, spec, canon, "ancho");
                    var h = Dim(d, spec, canon, "alto");
                    var e = Dim(d, spec, canon, "espesor");
                    if (e.Feet * 2 >= Math.Min(w.Feet, h.Feet)) throw new ToolException("perfil.espesor debe ser menor que la mitad del lado menor");
                    Rect(pd, prefix, "E", w, h);
                    var wi = Derived(d, (w.Param ?? prefix + "_ancho") + "_Int", w, e, w.Feet - 2 * e.Feet);
                    var hi = Derived(d, (h.Param ?? prefix + "_alto") + "_Int", h, e, h.Feet - 2 * e.Feet);
                    Rect(pd, prefix, "I", wi, hi);
                    break;
                }

                case "perfil_I":
                case "perfil_C":
                {
                    var h = Dim(d, spec, canon, "h");
                    var b = Dim(d, spec, canon, "b");
                    var tw = Dim(d, spec, canon, "tw");
                    var tf = Dim(d, spec, canon, "tf");
                    if (tf.Feet * 2 >= h.Feet) throw new ToolException("perfil.tf debe ser menor que la mitad de h");
                    if (tw.Feet >= b.Feet) throw new ToolException("perfil.tw debe ser menor que b");
                    string u1 = prefix + "_U1", u2 = prefix + "_U2", u3 = prefix + "_U3", u4 = prefix + "_U4";
                    string v1 = prefix + "_V1", v2 = prefix + "_V2", v3 = prefix + "_V3", v4 = prefix + "_V4";
                    Plane(pd, v1, 'V', -h.Feet / 2); Plane(pd, v4, 'V', h.Feet / 2);
                    Plane(pd, v2, 'V', -h.Feet / 2 + tf.Feet); Plane(pd, v3, 'V', h.Feet / 2 - tf.Feet);
                    pd.Dims.Add(new DimDef { A = v1, B = v4, Param = h.Param });
                    pd.Eqs.Add(new EqDef { Lo = v1, Hi = v4, Axis = 'V' });
                    pd.Dims.Add(new DimDef { A = v1, B = v2, Param = tf.Param });
                    pd.Dims.Add(new DimDef { A = v3, B = v4, Param = tf.Param });
                    var l = new LoopDef();
                    if (canon == "perfil_I")
                    {
                        Plane(pd, u1, 'U', -b.Feet / 2); Plane(pd, u4, 'U', b.Feet / 2);
                        Plane(pd, u2, 'U', -tw.Feet / 2); Plane(pd, u3, 'U', tw.Feet / 2);
                        pd.Dims.Add(new DimDef { A = u1, B = u4, Param = b.Param });
                        pd.Eqs.Add(new EqDef { Lo = u1, Hi = u4, Axis = 'U' });
                        pd.Dims.Add(new DimDef { A = u2, B = u3, Param = tw.Param });
                        pd.Eqs.Add(new EqDef { Lo = u2, Hi = u3, Axis = 'U' });
                        foreach (var v in new[] { new[] { u1, v1 }, new[] { u4, v1 }, new[] { u4, v2 }, new[] { u3, v2 }, new[] { u3, v3 },
                                                  new[] { u4, v3 }, new[] { u4, v4 }, new[] { u1, v4 }, new[] { u1, v3 }, new[] { u2, v3 },
                                                  new[] { u2, v2 }, new[] { u1, v2 } })
                            l.Vertices.Add(v);
                    }
                    else
                    {
                        Plane(pd, u1, 'U', -b.Feet / 2); Plane(pd, u3, 'U', b.Feet / 2); Plane(pd, u2, 'U', -b.Feet / 2 + tw.Feet);
                        pd.Dims.Add(new DimDef { A = u1, B = u3, Param = b.Param });
                        pd.Eqs.Add(new EqDef { Lo = u1, Hi = u3, Axis = 'U' });
                        pd.Dims.Add(new DimDef { A = u1, B = u2, Param = tw.Param });
                        foreach (var v in new[] { new[] { u1, v1 }, new[] { u3, v1 }, new[] { u3, v2 }, new[] { u2, v2 },
                                                  new[] { u2, v3 }, new[] { u3, v3 }, new[] { u3, v4 }, new[] { u1, v4 } })
                            l.Vertices.Add(v);
                    }
                    pd.Loops.Add(l);
                    break;
                }

                case "perfil_L":
                {
                    var h = Dim(d, spec, canon, "h");
                    var b = Dim(d, spec, canon, "b");
                    var t = Dim(d, spec, canon, "t");
                    if (t.Feet >= Math.Min(h.Feet, b.Feet)) throw new ToolException("perfil.t debe ser menor que h y que b");
                    string u1 = prefix + "_U1", u2 = prefix + "_U2", u3 = prefix + "_U3";
                    string v1 = prefix + "_V1", v2 = prefix + "_V2", v3 = prefix + "_V3";
                    Plane(pd, u1, 'U', -b.Feet / 2); Plane(pd, u3, 'U', b.Feet / 2); Plane(pd, u2, 'U', -b.Feet / 2 + t.Feet);
                    Plane(pd, v1, 'V', -h.Feet / 2); Plane(pd, v3, 'V', h.Feet / 2); Plane(pd, v2, 'V', -h.Feet / 2 + t.Feet);
                    pd.Dims.Add(new DimDef { A = u1, B = u3, Param = b.Param });
                    pd.Eqs.Add(new EqDef { Lo = u1, Hi = u3, Axis = 'U' });
                    pd.Dims.Add(new DimDef { A = u1, B = u2, Param = t.Param });
                    pd.Dims.Add(new DimDef { A = v1, B = v3, Param = h.Param });
                    pd.Eqs.Add(new EqDef { Lo = v1, Hi = v3, Axis = 'V' });
                    pd.Dims.Add(new DimDef { A = v1, B = v2, Param = t.Param });
                    var l = new LoopDef();
                    foreach (var v in new[] { new[] { u1, v1 }, new[] { u3, v1 }, new[] { u3, v2 }, new[] { u2, v2 }, new[] { u2, v3 }, new[] { u1, v3 } })
                        l.Vertices.Add(v);
                    pd.Loops.Add(l);
                    break;
                }
            }
            if (spec["centro"] is JArray center)
            {
                if (center.Count != 2) throw new ToolException("perfil.centro debe ser [u, v]");
                ApplyCenter(pd, prefix, 'U', Signed(d, center[0]));
                ApplyCenter(pd, prefix, 'V', Signed(d, center[1]));
            }
            return pd;
        }
    }
}
