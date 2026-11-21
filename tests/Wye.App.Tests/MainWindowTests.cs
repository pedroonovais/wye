using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Wye.App.ViewModels;
using Wye.App.Views;
using Wye.Data;
using Wye.Git;

[assembly: AvaloniaTestApplication(typeof(Wye.App.Tests.TestAppBuilder))]

namespace Wye.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Teste de interface sem tela: abre a janela real sobre um repositório temporário,
/// faz stage parcial e commit pelos comandos da UI e salva capturas em PNG.
/// Defina WYE_SCREENSHOTS=&lt;pasta&gt; para escolher onde salvar.
/// </summary>
public sealed class MainWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wye-ui", Guid.NewGuid().ToString("N"));

    private static string ShotsDir =>
        Environment.GetEnvironmentVariable("WYE_SCREENSHOTS") is { Length: > 0 } d
            ? d
            : Path.Combine(AppContext.BaseDirectory, "screenshots");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Tempo esgotado esperando: {what}");
            }

            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Save(MainWindow window, string name)
    {
        Directory.CreateDirectory(ShotsDir);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.CaptureRenderedFrame()?.Save(Path.Combine(ShotsDir, name));
    }

    [AvaloniaFact]
    public async Task Stage_lines_and_commit_through_the_ui()
    {
        // Repositório de exemplo com um commit e mudanças em dois lugares do mesmo arquivo.
        var git = await GitLocator.CreateRunnerAsync();
        var repoPath = Path.Combine(_root, "demo");
        var repo = await GitRepository.InitAsync(git, repoPath);
        foreach (var (k, v) in new[] { ("user.name", "Demo"), ("user.email", "demo@example.com"), ("commit.gpgsign", "false") })
        {
            await git.RunAsync(repoPath, ["config", k, v]);
        }

        var file = Path.Combine(repoPath, "Program.cs");
        await File.WriteAllTextAsync(file,
            "using System;\n\nclass Program\n{\n    static void Main()\n    {\n        Console.WriteLine(\"Olá\");\n    }\n}\n");
        await repo.StageFilesAsync(["Program.cs"]);
        await repo.CommitAsync("Commit inicial");
        await File.WriteAllTextAsync(file,
            "using System;\nusing System.Linq;\n\nclass Program\n{\n    static void Main()\n    {\n        Console.WriteLine(\"Olá, wye!\");\n    }\n}\n");
        await File.WriteAllTextAsync(Path.Combine(repoPath, "README.md"), "# Demo\n");

        var store = new RepositoryStore(Path.Combine(_root, "wye.db"));
        store.Add(repoPath);
        store.SetSetting("last-repository", Path.GetFullPath(repoPath));

        var vm = new MainWindowViewModel(store);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();

        await vm.InitializeAsync();
        await WaitUntil(() => vm.Files.Count == 2, "status com 2 arquivos");

        vm.SelectedFile = vm.Files.Single(f => f.Path == "Program.cs");
        await WaitUntil(() => vm.DiffLines.Count > 0, "diff do Program.cs");

        // Seleciona só a linha adicionada "using System.Linq;" e prepara.
        vm.DiffLines.Single(l => l.IsAdd && l.Text == "using System.Linq;").IsSelected = true;
        Save(window, "1-changes.png");

        await vm.StageSelectedLinesCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Files.Single(f => f.Path == "Program.cs").StageState is null, "arquivo parcialmente preparado");

        var index = (await git.RunAsync(repoPath, ["show", ":Program.cs"])).Stdout;
        Assert.Contains("using System.Linq;", index);
        Assert.Contains("Console.WriteLine(\"Olá\");", index); // a outra mudança não foi junto

        vm.CommitSummary = "Adiciona using System.Linq";
        Assert.True(vm.CommitCommand.CanExecute(null));
        await vm.CommitCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.StatusText.StartsWith("Commit", StringComparison.Ordinal), "commit concluído");

        var log = await repo.GetCommitsAsync();
        Assert.Equal("Adiciona using System.Linq", log[0].Summary);
        Assert.Equal(2, vm.Files.Count); // Program.cs (resto) + README.md continuam pendentes

        vm.SelectedTabIndex = 1;
        await WaitUntil(() => vm.Commits.Count == 2 && vm.DiffLines.Count > 0, "histórico com diff");
        vm.SelectedCommit = vm.Commits[0];
        await WaitUntil(() => vm.DiffLines.Count > 0, "diff do último commit");
        Save(window, "2-history.png");

        vm.SelectedTabIndex = 0;
        vm.SelectedFile = vm.Files.Single(f => f.Path == "Program.cs");
        await WaitUntil(() => vm.DiffLines.Count > 0, "diff do Program.cs de novo");
        vm.DiffLines.First(l => l.IsDelete).IsSelected = true;
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        Save(window, "3-changes-dark.png");
        Application.Current.RequestedThemeVariant = ThemeVariant.Default;

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task Shows_welcome_screen_without_repositories()
    {
        var store = new RepositoryStore(Path.Combine(_root, "wye.db"));
        var vm = new MainWindowViewModel(store);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();

        await vm.InitializeAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.HasRepository);
        Assert.True(vm.HasNoRepository);
        Assert.Equal("wye", vm.WindowTitle);
        Save(window, "0-welcome.png");

        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        Save(window, "0-welcome-dark.png");
        Application.Current.RequestedThemeVariant = ThemeVariant.Default;

        window.Close();
        vm.Dispose();
    }
}
