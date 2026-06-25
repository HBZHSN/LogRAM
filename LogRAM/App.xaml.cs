using System;
using System.IO;
using System.Windows;

namespace LogRAM
{
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var window = new MainWindow();
            window.Show();

            if (e.Args.Length > 0)
            {
                var filePath = e.Args[0];
                if (!Path.IsPathFullyQualified(filePath))
                {
                    filePath = Path.GetFullPath(filePath);
                }

                if (File.Exists(filePath))
                {
                    await window.OpenFileFromArgsAsync(filePath);
                }
            }
        }
    }
}
