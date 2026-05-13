using Autodesk.Revit.DB;
using System.Collections.Generic;

namespace RevitMcpPlugin
{
    /// <summary>
    /// Capa de compatibilidad entre versiones de la API de Revit.
    ///
    /// Constantes de compilación usadas:
    ///   REVIT_PRE2024 — Revit 2021/2022/2023: ElementId solo expone IntegerValue (int)
    ///   REVIT_NETFW   — Revit 2021-2024 (net48): faltan helpers de .NET 5+ en Dictionary
    ///   REVIT2024     — Revit 2024 (net48): ElementId.Value ya disponible, pero sin helpers
    ///   REVIT2025     — Revit 2025 (net8): API completa, sin restricciones
    /// </summary>
    internal static class RevitApiCompat
    {
        // ── ElementId → long ────────────────────────────────────────────────────
        //   Revit 2021-2023 : ElementId.IntegerValue (int)   → REVIT_PRE2024
        //   Revit 2024-2025 : ElementId.Value        (long)
        public static long GetId(this ElementId elementId)
        {
#if REVIT_PRE2024
            return elementId.IntegerValue;
#else
            return elementId.Value;
#endif
        }

#if REVIT_NETFW
        // ── Dictionary.GetValueOrDefault ────────────────────────────────────────
        //   Disponible desde .NET 5+. En .NET Framework 4.8 (Revit 2021-2024)
        //   hay que implementarlo manualmente.
        public static TValue GetValueOrDefault<TKey, TValue>(
            this Dictionary<TKey, TValue> dict,
            TKey key,
            TValue defaultValue = default)
        {
            return dict.TryGetValue(key, out var val) ? val : defaultValue;
        }
#endif
    }
}
