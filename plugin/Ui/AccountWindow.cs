#nullable disable
using System;
using System.Windows;
using System.Windows.Controls;
using RevitMcpPlugin.Auth;

namespace RevitMcpPlugin.Ui
{
    /// <summary>Inicio de sesión con la cuenta DJP (Supabase) y, con la sesión abierta, estado de la cuenta y cierre de sesión.</summary>
    public sealed class AccountWindow : Window
    {
        private readonly Border _host = new Border();
        private TextBox _email;
        private PasswordBox _password;
        private TextBlock _error;
        private Button _submit;
        private Button _cancel;

        public bool SignedIn { get; private set; }

        public AccountWindow()
        {
            DjpTheme.Apply(this);
            Title = "RevitMCP — Cuenta DJP";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Content = _host;

            if (AuthManager.GetCurrentUserEmail() != null && AuthManager.IsAuthenticated()) ShowAccount();
            else ShowLogin();
        }

        // ── Vista: iniciar sesión ──────────────────────────────────────────────
        private void ShowLogin()
        {
            var body = new StackPanel { Margin = new Thickness(24, 20, 24, 8) };
            body.Children.Add(DjpUi.Body("Usa el correo y la contraseña de tu cuenta DJP para activar RevitMCP en este equipo."));

            var emailLabel = DjpUi.Label("Correo electrónico");
            emailLabel.Margin = new Thickness(0, 16, 0, 4);
            body.Children.Add(emailLabel);
            _email = DjpUi.Input();
            body.Children.Add(_email);

            var passLabel = DjpUi.Label("Contraseña");
            passLabel.Margin = new Thickness(0, 12, 0, 4);
            body.Children.Add(passLabel);
            _password = DjpUi.Password();
            body.Children.Add(_password);

            _error = new TextBlock
            {
                Foreground = (System.Windows.Media.Brush)DjpTheme.Resources["Danger"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
                Visibility = Visibility.Collapsed
            };
            body.Children.Add(_error);

            _submit = DjpUi.Button("Iniciar sesión", ButtonKind.Primary, async (s, e) => await SubmitAsync());
            _submit.IsDefault = true;
            _cancel = DjpUi.Button("Ahora no", ButtonKind.Ghost, (s, e) => Close());
            _cancel.IsCancel = true;

            var root = new DockPanel();
            var header = DjpUi.Header("Cuenta DJP", "Inicia sesión para usar RevitMCP");
            DockPanel.SetDock(header, Dock.Top);
            var footer = DjpUi.Footer(_cancel, _submit);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(header);
            root.Children.Add(footer);
            root.Children.Add(body);
            _host.Child = root;
            _email.Focus();
        }

        private async System.Threading.Tasks.Task SubmitAsync()
        {
            var email = _email.Text.Trim();
            var password = _password.Password;
            if (email.Length == 0 || password.Length == 0) { ShowError("Escribe tu correo y tu contraseña."); return; }

            SetBusy(true);
            // La continuación vuelve al hilo de la interfaz: SignInAsync no usa ConfigureAwait(true), pero este await sí captura el contexto de WPF.
            var result = await AuthManager.SignInAsync(email, password);
            SetBusy(false);

            if (result.Success) { SignedIn = true; ShowAccount(); }
            else ShowError(result.Message);
        }

        private void SetBusy(bool busy)
        {
            _email.IsEnabled = !busy;
            _password.IsEnabled = !busy;
            _submit.IsEnabled = !busy;
            ((TextBlock)_submit.Content).Text = busy ? "Verificando…" : "Iniciar sesión";
            if (busy) _error.Visibility = Visibility.Collapsed;
        }

        private void ShowError(string message)
        {
            _error.Text = message;
            _error.Visibility = Visibility.Visible;
            _password.Clear();
            _password.Focus();
        }

        // ── Vista: cuenta con sesión abierta ───────────────────────────────────
        private void ShowAccount()
        {
            var body = new StackPanel { Margin = new Thickness(24, 20, 24, 8) };
            var email = AuthManager.GetCurrentUserEmail() ?? "";
            body.Children.Add(DjpUi.Label("Sesión iniciada como"));
            var emailText = DjpUi.Heading(email);
            emailText.Margin = new Thickness(0, 2, 0, 8);
            body.Children.Add(emailText);
            body.Children.Add(DjpUi.Badge("Activa", BadgeKind.Success));
            var note = DjpUi.Muted("La sesión se guarda cifrada en este equipo y se renueva sola. Cerrarla bloquea las herramientas hasta que vuelvas a entrar.");
            note.Margin = new Thickness(0, 14, 0, 0);
            body.Children.Add(note);

            var close = DjpUi.Button("Cerrar", ButtonKind.Secondary, (s, e) => Close());
            close.IsCancel = true;
            var signOut = DjpUi.Button("Cerrar sesión", ButtonKind.Danger, (s, e) => { AuthManager.SignOut(); SignedIn = false; ShowLogin(); });

            var root = new DockPanel();
            var header = DjpUi.Header("Cuenta DJP", "Tu sesión en RevitMCP");
            DockPanel.SetDock(header, Dock.Top);
            var footer = DjpUi.Footer(signOut, close);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(header);
            root.Children.Add(footer);
            root.Children.Add(body);
            _host.Child = root;
        }
    }
}
