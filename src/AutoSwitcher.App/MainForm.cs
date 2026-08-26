using Microsoft.Web.WebView2.WinForms;

namespace AutoSwitcher.App;

// Ventana nativa de Windows que muestra el dashboard (sin navegador, sin consola).
public class MainForm : Form
{
    public MainForm()
    {
        Text = "AutoSwitcher";
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Width = 860;
        Height = 940;
        MinimumSize = new Size(420, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(13, 13, 15);

        var wv = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(13, 13, 15) };
        Controls.Add(wv);
        wv.Source = new Uri("http://localhost:5000/?v=" + DateTime.Now.Ticks);
    }
}
