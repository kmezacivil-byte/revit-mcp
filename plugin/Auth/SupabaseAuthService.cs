#nullable disable
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace RevitMcpPlugin.Auth
{
    /// <summary>
    /// Cliente de la API REST de Supabase Auth (email + contraseña). Sigue la guía de autenticación DJP.
    /// Todos los await usan ConfigureAwait(false): el hilo principal de Revit/AutoCAD tiene SynchronizationContext y, si algún
    /// llamador espera esta tarea de forma síncrona, volver a ese hilo bloqueado provocaría un deadlock.
    /// </summary>
    public class SupabaseAuthService
    {
        private readonly string _url;
        private readonly HttpClient _http;

        public SupabaseAuthService(string url, string anonKey)
        {
            _url = url.TrimEnd('/');
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _http.DefaultRequestHeaders.Add("apikey", anonKey);
        }

        public Task<AuthResponse> SignInWithPassword(string email, string password) =>
            Post("password", new { email, password }, "Error de autenticación");

        public Task<AuthResponse> RefreshSession(string refreshToken) =>
            Post("refresh_token", new { refresh_token = refreshToken }, "La sesión expiró. Inicia sesión de nuevo.");

        private async Task<AuthResponse> Post(string grantType, object payload, string defaultError)
        {
            try
            {
                var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                var response = await _http.PostAsync($"{_url}/auth/v1/token?grant_type={grantType}", content).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var data = JsonConvert.DeserializeObject<SupabaseAuthData>(body);
                    return new AuthResponse
                    {
                        Success = true, AccessToken = data.access_token, RefreshToken = data.refresh_token,
                        ExpiresIn = data.expires_in, User = data.user
                    };
                }

                string message = null;
                try
                {
                    var err = JsonConvert.DeserializeObject<SupabaseError>(body);
                    message = err?.error_description ?? err?.msg ?? err?.error;
                }
                catch { /* cuerpo que no es JSON */ }
                return new AuthResponse { Success = false, ErrorMessage = Friendly(message) ?? defaultError };
            }
            catch (TaskCanceledException)
            {
                return new AuthResponse { Success = false, ErrorMessage = "No hubo respuesta del servidor. Revisa tu conexión a internet." };
            }
            catch (HttpRequestException ex)
            {
                return new AuthResponse { Success = false, ErrorMessage = "No se pudo conectar con el servidor de DJP: " + ex.Message };
            }
            catch (Exception ex)
            {
                return new AuthResponse { Success = false, ErrorMessage = "Error: " + ex.Message };
            }
        }

        /// <summary>Traduce los mensajes más comunes de Supabase.</summary>
        private static string Friendly(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return null;
            if (message.IndexOf("Invalid login credentials", StringComparison.OrdinalIgnoreCase) >= 0) return "Correo o contraseña incorrectos.";
            if (message.IndexOf("Email not confirmed", StringComparison.OrdinalIgnoreCase) >= 0) return "Tu correo aún no está confirmado.";
            return message;
        }
    }

    public class AuthResponse
    {
        public bool Success { get; set; }
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public int ExpiresIn { get; set; }
        public SupabaseUser User { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class SupabaseAuthData
    {
        public string access_token { get; set; }
        public int expires_in { get; set; }
        public string refresh_token { get; set; }
        public SupabaseUser user { get; set; }
    }

    public class SupabaseUser { public string id { get; set; } public string email { get; set; } }

    public class SupabaseError
    {
        public string error { get; set; }
        public string error_description { get; set; }
        public string msg { get; set; }
    }
}
