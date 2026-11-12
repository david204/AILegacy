using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using AILegacy.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AILegacy.Web
{
    /// <summary>
    /// El editor de AI Legacy en una ventana propia encima del juego (WebView2),
    /// que carga la página que sirve EditorServer.
    /// </summary>
    internal static class EditorWindow
    {
        private static Form _form;
        private static Thread _thread;

        public static void Show(string url)
        {
            try
            {
                if (_form != null && !_form.IsDisposed)
                {
                    _form.BeginInvoke(new Action(() => { _form.WindowState = FormWindowState.Normal; _form.Activate(); }));
                    return;
                }
                _thread = new Thread(() => Run(url)) { IsBackground = true, Name = "AILegacyEditorWindow" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
            catch (Exception ex) { Log.Error("No se pudo abrir la ventana del editor", ex); }
        }

        private static void Run(string url)
        {
            try
            {
                Application.EnableVisualStyles();
                var form = new Form
                {
                    Text = L.T("AI Legacy — Editor"),
                    StartPosition = FormStartPosition.CenterScreen,
                    Size = new Size(1600, 940),
                    MinimumSize = new Size(1000, 600),
                    BackColor = Color.FromArgb(22, 18, 13),
                    TopMost = true,
                    ShowInTaskbar = true,
                };
                var web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(22, 18, 13) };
                form.Controls.Add(web);
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
                        web.Source = new Uri(url);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("WebView2 no pudo arrancar", ex);
                        MessageBox.Show(form, L.T("No se pudo cargar el editor dentro del juego (WebView2).\n\nÁbrelo en el navegador: ") + url + "\n\n" + ex.Message,
                            "AI Legacy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };
                form.FormClosed += (s, e) => { _form = null; };
                _form = form;
                Application.Run(form);
            }
            catch (Exception ex)
            {
                Log.Error("Ventana del editor", ex);
                _form = null;
            }
        }
    }
}
