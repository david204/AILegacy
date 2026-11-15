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
    /// In-game editor: a borderless panel laid over the game image and owned by the game window, so it stays off the
    /// taskbar and moves and minimizes with the game. Closes with its own X button or Escape. Inside it's the page served
    /// by EditorServer, rendered with WebView2.
    /// </summary>
    internal static class EditorWindow
    {
        private static Form _form;
        private static WebView2 _web;
        private static Thread _thread;

        // Share of the game area the panel covers
        private const double WidthShare = 0.88, HeightShare = 0.90;
        private const int Frame = 2;                                         // gold border width, px
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
                    // already open: go to the new page (the editor, or a video of a scene)
                    _form.BeginInvoke(new Action(() => { try { if (_web?.CoreWebView2 != null) _web.Source = new Uri(url); } catch { } _form.Show(); _form.Activate(); }));
                    return;
                }
                IntPtr game = IntPtr.Zero;
                try { game = Process.GetCurrentProcess().MainWindowHandle; } catch { }
                _thread = new Thread(() => Run(url, game)) { IsBackground = true, Name = "AILegacyEditorWindow" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
            catch (Exception ex) { Log.Error("Could not open the editor", ex); }
        }

        /// <summary>Closes the panel if it's open. Called when leaving the campaign.</summary>
        public static void Close()
        {
            try { var f = _form; if (f != null && !f.IsDisposed) f.BeginInvoke(new Action(f.Close)); } catch { }
        }

        /// <summary>Panel bounds, centered on the game image. Falls back to the screen center if there's no game window.</summary>
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
                    FormBorderStyle = FormBorderStyle.None,        // no frame or title bar, it's meant to look like part of the game
                    StartPosition = FormStartPosition.Manual,
                    Bounds = Place(game),
                    BackColor = Gold,
                    Padding = new Padding(Frame),
                    ShowInTaskbar = false,
                    TopMost = false,
                    KeyPreview = true,
                };
                var web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Dark };
                _web = web;
                form.Controls.Add(web);

                // Make the game window the owner, so the panel stays above the game and nothing else
                form.HandleCreated += (s, e) =>
                {
                    try { if (game != IntPtr.Zero && IsWindow(game)) SetWindowLongPtr(form.Handle, GWLP_HWNDPARENT, game); }
                    catch (Exception ex) { Log.Warn("The editor could not attach to the game window: " + ex.Message); }
                };
                form.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) form.Close(); };

                // Follow the game window when it moves or resizes
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
                        // The page posts "close" from its X button or Escape
                        web.CoreWebView2.WebMessageReceived += (ws, we) =>
                        {
                            try { if (we.TryGetWebMessageAsString() == "close") form.BeginInvoke(new Action(form.Close)); } catch { }
                        };
                        web.Source = new Uri(url);
                        follow.Start();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("WebView2 could not start", ex);
                        MessageBox.Show(form, L.T("AIL_c774d780", "No se pudo cargar el editor dentro del juego (WebView2).\n\nÁbrelo en el navegador: ") + url + "\n\n" + ex.Message,
                            "AI Legacy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        form.Close();
                    }
                };
                form.FormClosed += (s, e) =>
                {
                    _form = null; _web = null;
                    try { follow.Stop(); follow.Dispose(); } catch { }
                    // Give focus back to the game
                    try { if (game != IntPtr.Zero && IsWindow(game)) SetForegroundWindow(game); } catch { }
                };
                _form = form;
                Application.Run(form);
            }
            catch (Exception ex)
            {
                Log.Error("Editor panel", ex);
                _form = null;
            }
        }
    }
}
