using System.Windows;
using SoftPilot.Infrastructure.Installation;

namespace SoftPilot.Setup;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!e.Args.Contains("--quiet", StringComparer.OrdinalIgnoreCase))
        {
            MainWindow = new SetupWindow();
            MainWindow.Show();
            return;
        }

        try
        {
            var parentIndex = Array.FindIndex(e.Args, argument =>
                string.Equals(argument, "--parent", StringComparison.OrdinalIgnoreCase));
            if (parentIndex < 0 || parentIndex + 1 >= e.Args.Length)
            {
                throw new ArgumentException("静默安装需要 --parent <父目录> 参数。");
            }

            var validation = new WindowsInstallationPathService().Validate(e.Args[parentIndex + 1]);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Errors));
            }

            await new InstallerEngine().InstallAsync(validation.FinalRoot);
            Shutdown(0);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            Shutdown(1);
        }
    }
}
