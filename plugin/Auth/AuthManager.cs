#nullable disable
using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace RevitMcpPlugin.Auth
{
    /// <summary>
    /// Sesión de Supabase de DJP: la guarda en el registro de Windows (HKCU\SOFTWARE\DJP\RevitMCP), la refresca antes de que
    /// caduque y la borra al cerrar sesión. A diferencia de la guía base (Base64), los tokens se protegen con DPAPI: solo se
    /// pueden descifrar con la misma cuenta de Windows en el mismo equipo.
    /// </summary>
    public static class AuthManager
    {
        // REVITMCP_AUTH_KEY permite usar otra clave de registro en pruebas automáticas.
        private static readonly string RegistryKey = Environment.GetEnvironmentVariable("REVITMCP_AUTH_KEY") ?? @"SOFTWARE\DJP\RevitMCP";
        private const int RefreshThresholdMinutes = 30;
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DJP.RevitMCP.v1");

        private static readonly SupabaseAuthService Service = new SupabaseAuthService(SupabaseConfig.Url, SupabaseConfig.AnonKey);

        // Evita golpear el registro (y la red) en cada llamada de herramienta.
        private static DateTime _validUntil = DateTime.MinValue;
        private static readonly object Gate = new object();

        // ── API pública ────────────────────────────────────────────────────────
        /// <summary>true si hay una sesión válida (refrescándola si le queda poco). Seguro de llamar desde cualquier hilo.</summary>
        public static bool IsAuthenticated()
        {
            lock (Gate) { if (DateTime.UtcNow < _validUntil) return true; }
            // Task.Run: ejecuta en el grupo de hilos, sin el SynchronizationContext del hilo principal (evita deadlocks)
            bool ok = Task.Run(IsAuthenticatedAsync).GetAwaiter().GetResult();
            lock (Gate) { _validUntil = ok ? DateTime.UtcNow.AddSeconds(30) : DateTime.MinValue; }
            return ok;
        }

        public static async Task<bool> IsAuthenticatedAsync()
        {
            try
            {
                var session = Read();
                if (session == null) return false;
                if (!NeedsRefresh(session)) return true;

                var refreshed = await Service.RefreshSession(session.RefreshToken).ConfigureAwait(false);
                if (refreshed.Success)
                {
                    Save(new SessionData
                    {
                        AccessToken = refreshed.AccessToken, RefreshToken = refreshed.RefreshToken,
                        ExpiresAtUtc = DateTime.UtcNow.AddSeconds(refreshed.ExpiresIn),
                        UserEmail = refreshed.User?.email ?? session.UserEmail
                    });
                    return true;
                }

                // Sin red no se cierra la sesión: se sigue mientras el token actual no haya caducado.
                if (refreshed.ErrorMessage != null && refreshed.ErrorMessage.StartsWith("La sesión expiró"))
                {
                    SignOut();
                    return false;
                }
                return session.ExpiresAtUtc > DateTime.UtcNow;
            }
            catch
            {
                var cached = Read();
                return cached != null && cached.ExpiresAtUtc > DateTime.UtcNow;
            }
        }

        public static async Task<AuthResult> SignInAsync(string email, string password)
        {
            try
            {
                var r = await Service.SignInWithPassword(email, password).ConfigureAwait(false);
                if (!r.Success) return new AuthResult { Success = false, Message = r.ErrorMessage };

                Save(new SessionData
                {
                    AccessToken = r.AccessToken, RefreshToken = r.RefreshToken,
                    ExpiresAtUtc = DateTime.UtcNow.AddSeconds(r.ExpiresIn), UserEmail = r.User?.email ?? email
                });
                lock (Gate) { _validUntil = DateTime.UtcNow.AddSeconds(30); }
                return new AuthResult { Success = true, Message = "Autenticación exitosa" };
            }
            catch (Exception ex)
            {
                return new AuthResult { Success = false, Message = ex.Message };
            }
        }

        public static void SignOut()
        {
            lock (Gate) { _validUntil = DateTime.MinValue; }
            try { Registry.CurrentUser.DeleteSubKeyTree(RegistryKey, false); } catch { }
        }

        public static string GetCurrentUserEmail() => Read()?.UserEmail;

        /// <summary>Token de acceso (JWT de Supabase) para validar licencias en el servidor; null si no hay sesión.</summary>
        public static string GetAccessToken() => Read()?.AccessToken;

        // ── Registro de Windows ────────────────────────────────────────────────
        private static SessionData Read()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryKey))
                {
                    if (key == null) return null;
                    var access = key.GetValue("AccessToken") as string;
                    var refresh = key.GetValue("RefreshToken") as string;
                    var expires = key.GetValue("ExpiresAt") as string;
                    if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh) ||
                        !DateTime.TryParse(expires, null, System.Globalization.DateTimeStyles.RoundtripKind, out var exp))
                        return null;
                    return new SessionData
                    {
                        AccessToken = Unprotect(access), RefreshToken = Unprotect(refresh),
                        ExpiresAtUtc = exp.ToUniversalTime(), UserEmail = key.GetValue("UserEmail") as string
                    };
                }
            }
            catch { return null; }   // clave corrupta o de otro usuario: equivale a "sin sesión"
        }

        private static void Save(SessionData s)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegistryKey))
                {
                    if (key == null) return;
                    key.SetValue("AccessToken", Protect(s.AccessToken));
                    key.SetValue("RefreshToken", Protect(s.RefreshToken));
                    key.SetValue("ExpiresAt", s.ExpiresAtUtc.ToString("O"));
                    key.SetValue("UserEmail", s.UserEmail ?? "");
                }
            }
            catch { }
        }

        private static bool NeedsRefresh(SessionData s) => s.ExpiresAtUtc - DateTime.UtcNow < TimeSpan.FromMinutes(RefreshThresholdMinutes);

        private static string Protect(string text) =>
            Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), Entropy, DataProtectionScope.CurrentUser));

        private static string Unprotect(string text) =>
            Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(text), Entropy, DataProtectionScope.CurrentUser));
    }

    public class SessionData
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public string UserEmail { get; set; }
    }

    public class AuthResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }
}
