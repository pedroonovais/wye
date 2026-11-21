using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Wye.App.ViewModels;

namespace Wye.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.PickFolder = PickFolderAsync;
            }
        };

        // Como o Desktop, atualiza o status quando a janela volta a ter foco
        // (cobre o caso em que o watcher de arquivos não pôde ser criado).
        Activated += (_, _) => (DataContext as MainWindowViewModel)?.RefreshCommand.Execute(null);
    }

    // Fecha o seletor de repositório ou de branch depois que a pessoa escolhe um item.
    // Mudanças feitas pelo próprio app (atualização em segundo plano) não fecham nada.
    private void OnPickerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || sender is not ListBox { IsKeyboardFocusWithin: true } and not ListBox { IsPointerOver: true })
        {
            return;
        }

        RepositoryCell.Flyout?.Hide();
        BranchCell.Flyout?.Hide();
    }

    private async Task<string?> PickFolderAsync()
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Escolha a pasta do repositório",
            AllowMultiple = false,
        });

        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }
}
