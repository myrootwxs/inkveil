using System;
using System.Windows;

namespace Inkveil
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;

            var window = new MainWindow(args);
            app.MainWindow = window;
            app.Run(window);
        }
    }
}
