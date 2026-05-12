using Autodesk.Revit.DB;
using System.Collections.Generic;

namespace RevitMcpPlugin
{
    internal static class RevitApiCompat
    {
        // ElementId.Value (long) existe desde Revit 2024.
        // Revit 2021 solo expone IntegerValue (int).
        public static long GetId(this ElementId elementId)
        {
#if REVIT2021
            return elementId.IntegerValue;
#else
            return elementId.Value;
#endif
        }

#if REVIT2021
        // Dictionary.GetValueOrDefault fue agregado en .NET Core 2.0 / .NET 5+.
        // En .NET Framework 4.8 hay que implementarlo manualmente.
        public static TValue GetValueOrDefault<TKey, TValue>(
            this Dictionary<TKey, TValue> dict, TKey key, TValue defaultValue)
        {
            return dict.TryGetValue(key, out var val) ? val : defaultValue;
        }
#endif
    }
}
