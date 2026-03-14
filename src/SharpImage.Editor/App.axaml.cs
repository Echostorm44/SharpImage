using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SharpImage.Editor.Windows;

namespace SharpImage.Editor;

public partial class App : Application
{
    public static string? StartupPath { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.Args is { Length: > 0 } && !string.IsNullOrWhiteSpace(desktop.Args[0]))
                StartupPath = desktop.Args[0];

            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
