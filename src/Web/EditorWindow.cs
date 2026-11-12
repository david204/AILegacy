using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using AILegacy.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AILegacy.Web
{
    /// <summary>
    /// El editor de AI Legacy DENTRO del juego: un panel sin marco de ventana, encajado sobre la imagen del juego, que
    /// pertenece a la ventana del juego (no sale en la barra de tareas, se mueve y se minimiza con ella) y se cierra con
    /// su propia aspa o con Escape. Por dentro es la página que sirve EditorServer, dibujada con WebView2.
    /// </summary>
    internal static class EditorWindow
    {
        private static Form _form;
        private static Thread _thread;

        // Cuánto del área del juego ocupa el panel
        private const double WidthShare = 0.88, HeightShare = 0.90;
        private const int Frame = 2;                                         // filete dorado alrededor
        private static readonly Color Gold = Color.FromArgb(196, 160, 96);
        private static readonly Color Dark = Color.FromArgb(22, 18, 13);

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
        private const int GWLP_HWNDPARENT = -8;

        public static void Show(string url)
        {
            try
            {
                if (_form != null && !_form.IsDisposed)
                {
                    _form.BeginInvoke(new Action(() => { _form.Show(); _form.Activate(); }));
                    return;
                }
                IntPtr game = IntPtr.Zero;
                try { game = Process.GetCurrentProcess().MainWindowHandle; } catch { }
                _thread = new Thread(() => Run(url, game)) { IsBackground = true, Name = "AILegacyEditorWindow" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
            catch (Exception ex) { Log.Error("No se pudo abrir el editor", ex); }
        }

        /// <summary>Cierra el panel si está abierto (al salir de la partida).</summary>
        public static void Close()
        {
            try { var f = _form; if (f != null && !f.IsDisposed) f.BeginInvoke(new Action(f.Close)); } catch { }
        }

        /// <summary>El rectángulo del panel, centrado sobre la imagen del juego. Sin ventana de juego: centrado en la pantalla.</summary>
        private static Rectangle Place(IntPtr game)
        {
            try
            {
                if (game != IntPtr.Zero && IsWindow(game) && !IsIconic(game) && GetClientRect(game, out var r))
                {
                    var origin = new POINT { X = 0, Y = 0 };
                    ClientToScreen(game, ref origin);
                    int w = r.Right - r.Left, h = r.Bottom - r.Top;
                    if (w > 400 && h > 300)
                    {
                        int pw = Math.Max(900, (int)(w * WidthShare)), ph = Math.Max(560, (int)(h * HeightShare));
                        pw = Math.Min(pw, w); ph = Math.Min(ph, h);
                        return new Rectangle(origin.X + (w - pw) / 2, origin.Y + (h - ph) / 2, pw, ph);
                    }
                }
            }
            catch { }
            var s = Screen.PrimaryScreen.WorkingArea;
            int sw = Math.Min(1600, (int)(s.Width * WidthShare)), sh = Math.Min(940, (int)(s.Height * HeightShare));
            return new Rectangle(s.Left + (s.Width - sw) / 2, s.Top + (s.Height - sh) / 2, sw, sh);
        }

        private static void Run(string url, IntPtr game)
        {
            try
            {
                Application.EnableVisualStyles();
                var form = new Form
                {
                    Text = L.T("AIL_a9672a5d", "AI Legacy — Editor"),
                    FormBorderStyle = FormBorderStyle.None,        // sin marco ni barra de título: es un panel del juego
                    StartPosition = FormStartPosition.Manual,
                    Bounds = Place(game),
                    BackColor = Gold,
                    Padding = new Padding(Frame),
                    ShowInTaskbar = false,
                    TopMost = false,
                    KeyPreview = true,
                };
                var web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Dark };
                form.Controls.Add(web);

                // La ventana del juego es su dueña: el panel queda siempre encima de ella y solo de ella
                form.HandleCreated += (s, e) =>
                {
                    try { if (game != IntPtr.Zero && IsWindow(game)) SetWindowLongPtr(form.Handle, GWLP_HWNDPARENT, game); }
                    catch (Exception ex) { Log.Warn("El editor no pudo engancharse a la ventana del juego: " + ex.Message); }
                };
                form.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) form.Close(); };

                // Sigue a la ventana del juego si se mueve o cambia de tamaño
                var follow = new System.Windows.Forms.Timer { Interval = 300 };
                follow.Tick += (s, e) =>
                {
                    try
                    {
                        if (game == IntPtr.Zero) return;
                        if (!IsWindow(game)) { form.Close(); return; }
                        if (IsIconic(game)) return;
                        var want = Place(game);
                        if (form.Bounds != want) form.Bounds = want;
                    }
                    catch { }
                };

                form.Load += async (s, e) =>
                {
                    try
                    {
                        var dataDir = Path.Combine(Paths.ModuleDir, "web", "userdata");
                        Directory.CreateDirectory(dataDir);
                        var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
                        await web.EnsureCoreWebView2Async(env);
                        web.CoreWebView2.Settings.AreDevToolsEnabled = false;
                        web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                        web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                        // La página pide cerrarse (su aspa o la tecla Escape)
                        web.CoreWebView2.WebMessageReceived += (ws, we) =>
                        {
                            try { if (we.TryGetWebMessageAsString() == "close") form.BeginInvoke(new Action(form.Close)); } catch { }
                        };
                        web.Source = new Uri(url);
                        follow.Start();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("WebView2 no pudo arrancar", ex);
                        MessageBox.Show(form, L.T("AIL_c774d780", "No se pudo cargar el editor dentro del juego (WebView2).\n\nÁbrelo en el navegador: ") + url + "\n\n" + ex.Message,
                            "AI Legacy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        form.Close();
                    }
                };
                form.FormClosed += (s, e) =>
                {
                    _form = null;
                    try { follow.Stop(); follow.Dispose(); } catch { }
                    // El foco vuelve al juego
                    try { if (game != IntPtr.Zero && IsWindow(game)) SetForegroundWindow(game); } catch { }
                };
                _form = form;
                Application.Run(form);
            }
            catch (Exception ex)
            {
                Log.Error("Panel del editor", ex);
                _form = null;
            }
        }
    }
}
