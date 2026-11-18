using System.Windows;

namespace LiteDocViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MainWindow = new MainWindow(e.Args.FirstOrDefault());
        MainWindow.Show();
    }
}
