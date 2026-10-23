#nullable disable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace RevitMcpPlugin
{
    /// <summary>
    /// Ayuda con el túnel de Cloudflare ("cloudflared") que necesita ChatGPT para llegar a Revit: lo localiza, lo inicia
    /// con la dirección correcta y lee la dirección pública que obtiene. No instala nada ni toca la red por su cuenta:
    /// solo arranca el cloudflared que el usuario ya tiene, y solo cuando este pulsa "Iniciar túnel".
    /// </summary>
    internal static class TunnelManager
    {
        private static readonly Regex UrlPattern = new Regex(@"https://[a-zA-Z0-9-]+\.trycloudflare\.com", RegexOptions.Compiled);
        private static Process _process;

        public static bool Running => _process != null && !_process.HasExited;

        /// <summary>Dirección del túnel que cloudflared ya está sirviendo (iniciado por nosotros o a mano), o null.</summary>
        public static string Detect()
        {
            // cloudflared publica su dirección en el servidor de métricas local (puerto 20241 y siguientes)
            for (int port = 20241; port <= 20245; port++)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create($"http://127.0.0.1:{port}/quicktunnel");
                    req.Timeout = 800;
                    req.ReadWriteTimeout = 800;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var reader = new StreamReader(resp.GetResponseStream()))
                    {
                        var m = Regex.Match(reader.ReadToEnd(), "\"hostname\"\\s*:\\s*\"([^\"]+)\"");
                        if (m.Success && m.Groups[1].Value.Length > 0) return "https://" + m.Groups[1].Value;
                    }
                }
                catch { /* ese puerto no es de cloudflared */ }
            }
            return null;
        }

        /// <summary>Busca cloudflared: ruta guardada, PATH, carpeta Descargas y ubicaciones habituales.</summary>
        public static string FindExecutable(string savedPath)
        {
            if (!string.IsNullOrEmpty(savedPath) && File.Exists(savedPath)) return savedPath;

            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), "cloudflared.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }

            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            try
            {
                if (Directory.Exists(downloads))
                {
                    var found = Directory.GetFiles(downloads, "cloudflared*.exe")
                        .OrderByDescending(f => new FileInfo(f).LastWriteTime).FirstOrDefault();
                    if (found != null) return found;
                }
            }
            catch { }

            foreach (var folder in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            })
            {
                var candidate = Path.Combine(folder, "cloudflared", "cloudflared.exe");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>Inicia un túnel rápido hacia el servidor local. onUrl/onError se llaman desde otro hilo.</summary>
        public static void Start(string executable, int port, Action<string> onUrl, Action<string> onError)
        {
            Stop();
            var psi = new ProcessStartInfo(executable, $"tunnel --no-autoupdate --url http://127.0.0.1:{port}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,    // cloudflared escribe su registro en stderr
                RedirectStandardOutput = true
            };

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            DataReceivedEventHandler parse = (s, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                var m = UrlPattern.Match(e.Data);
                if (m.Success) onUrl(m.Value);
            };
            process.ErrorDataReceived += parse;
            process.OutputDataReceived += parse;
            process.Exited += (s, e) => onError("cloudflared se cerró (código " + SafeExitCode(process) + ").");

            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            _process = process;
        }

        private static string SafeExitCode(Process p)
        {
            try { return p.ExitCode.ToString(); } catch { return "?"; }
        }

        public static void Stop()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.EnableRaisingEvents = false;
                    _process.Kill();
                }
            }
            catch { }
            _process = null;
        }
    }
}
