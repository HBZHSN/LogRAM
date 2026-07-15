using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace LogRAM
{
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += App_DispatcherUnhandledException;

            try
            {
                if (CliRunner.IsCliInvocation(e.Args))
                {
                    var exitCode = await CliRunner.RunAsync(e.Args);
                    Shutdown(exitCode);
                    return;
                }

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
            catch (Exception ex)
            {
                ShowUnhandledError(ex);
                Shutdown(1);
            }
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            ShowUnhandledError(e.Exception);
            Shutdown(1);
        }

        private static void ShowUnhandledError(Exception exception)
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LogRAM",
                "error.log");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.WriteAllText(logPath, exception.ToString());
            }
            catch
            {
                logPath = "无法写入错误日志";
            }

            MessageBox.Show(
                $"LogRAM 无法启动：{exception.Message}\n\n详细错误：{logPath}",
                "LogRAM",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
