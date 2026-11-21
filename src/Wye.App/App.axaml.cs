using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wye.App.ViewModels;
using Wye.App.Views;
using Wye.Data;

namespace Wye.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var store = new RepositoryStore(RepositoryStore.DefaultDatabasePath);
            var vm = new MainWindowViewModel(store);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += (_, _) => vm.Dispose();
            _ = vm.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
