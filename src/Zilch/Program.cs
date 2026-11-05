using System;
using System.Linq;
using System.Windows.Forms;

namespace Zilch
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) =>
                MessageBox.Show(e.Exception.Message, "Zilch hit an error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            bool sudo = args.Any(a => a.Equals("--sudo", StringComparison.OrdinalIgnoreCase));
            Application.Run(new MainForm(sudo));
        }
    }
}
