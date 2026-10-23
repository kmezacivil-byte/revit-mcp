#nullable disable
using System;

namespace RevitMcpPlugin.Auth
{
    /// <summary>
    /// Proyecto Supabase de DJP. La clave "anon" está pensada para ir en aplicaciones cliente: no da acceso por sí sola, lo que
    /// protege los datos son las políticas RLS del proyecto. Aun así conviene no repetirla por todos lados: está solo aquí y
    /// puede sustituirse con las variables de entorno DJP_SUPABASE_URL y DJP_SUPABASE_ANON_KEY.
    /// </summary>
    internal static class SupabaseConfig
    {
        public static string Url =>
            Environment.GetEnvironmentVariable("DJP_SUPABASE_URL") ?? "https://nkoapijyvgjxgztfuljo.supabase.co";

        public static string AnonKey =>
            Environment.GetEnvironmentVariable("DJP_SUPABASE_ANON_KEY") ??
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im5rb2FwaWp5dmdqeGd6dGZ1bGpvIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NTkyNTc5ODksImV4cCI6MjA3NDgzMzk4OX0.P6weeMqd5pUtrKo2pOSAMLTfxSDodFYhL6HEloj89Mk";
    }
}
