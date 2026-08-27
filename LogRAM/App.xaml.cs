using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace LogRAM
{
    public partial class App : Application
    {
        private SingleInstanceCoordinator? _singleInstance;

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

                var filePaths = GetExistingFilePaths(e.Args);
                _singleInstance = new SingleInstanceCoordinator();
                if (!_singleInstance.IsPrimary)
                {
                    if (await _singleInstance.TryForwardAsync(filePaths))
                    {
                        _singleInstance.Dispose();
                        _singleInstance = null;
                        Shutdown();
                        return;
                    }

                    _singleInstance.Dispose();
                    _singleInstance = null;
                }

                var window = new MainWindow();
                window.Show();
                _singleInstance?.Start(filePath =>
                {
                    if (!Dispatcher.HasShutdownStarted)
                    {
                        Dispatcher.BeginInvoke(new Action(() => _ = window.HandleExternalOpenAsync(filePath)));
                    }
                });

                foreach (var filePath in filePaths)
                {
                    await window.OpenFileFromArgsAsync(filePath);
                }
            }
            catch (Exception ex)
            {
                ShowUnhandledError(ex);
                Shutdown(1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _singleInstance?.Dispose();
            base.OnExit(e);
        }

        private static IReadOnlyList<string> GetExistingFilePaths(IEnumerable<string> arguments)
        {
            return arguments
                .Select(path => Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(path))
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
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
