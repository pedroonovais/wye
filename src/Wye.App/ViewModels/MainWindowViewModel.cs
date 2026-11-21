using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wye.App.Services;
using Wye.Core.Models;
using Wye.Data;
using Wye.Git;

namespace Wye.App.ViewModels;

/// <summary>
/// Estado e ações da janela principal. Faz o papel que no GitHub Desktop é dividido entre
/// Dispatcher (ui/dispatcher/dispatcher.ts) e AppStore (lib/stores/app-store.ts): no MVVM os
/// comandos são as ações e as propriedades observáveis substituem o emitUpdate() + re-render.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly RepositoryStore _store;
    private GitRunner? _git;
    private GitRepository? _repo;
    private RepositoryWatcher? _watcher;
    private BackgroundFetcher? _fetcher;
    private FileDiff _currentDiff = FileDiff.Empty;
    private bool _suppressBranchCheckout;
    private int _refreshGeneration;
    private int _diffGeneration;

    public MainWindowViewModel(RepositoryStore store)
    {
        _store = store;
        Files.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(FilesSummary));
            OnPropertyChanged(nameof(IsClean));
        };
    }

    /// <summary>Fornecido pela View: abre o seletor de pastas do sistema.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    public ObservableCollection<SavedRepository> Repositories { get; } = [];
    public ObservableCollection<Branch> Branches { get; } = [];
    public ObservableCollection<FileItemViewModel> Files { get; } = [];
    public ObservableCollection<DiffLineViewModel> DiffLines { get; } = [];
    public ObservableCollection<CommitItemViewModel> Commits { get; } = [];
    public ObservableCollection<CommitFileItemViewModel> CommitFiles { get; } = [];

    [ObservableProperty] private SavedRepository? _selectedRepository;
    [ObservableProperty] private Branch? _selectedBranch;
    [ObservableProperty] private FileItemViewModel? _selectedFile;
    [ObservableProperty] private CommitItemViewModel? _selectedCommit;
    [ObservableProperty] private CommitFileItemViewModel? _selectedCommitFile;

    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private bool _showStaged;

    [ObservableProperty] private string _branchName = "—";
    [ObservableProperty] private int _ahead;
    [ObservableProperty] private int _behind;
    [ObservableProperty] private bool _hasUpstream;
    [ObservableProperty] private bool _hasRemote;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private string _commitSummary = string.Empty;

    [ObservableProperty] private string _commitDescription = string.Empty;
    [ObservableProperty] private string _newBranchName = string.Empty;

    [ObservableProperty] private string? _diffMessage;
    [ObservableProperty] private string? _diffTitle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty] private string _statusText = "Pronto";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private int _stagedCount;

    public bool IsIdle => !IsBusy;
    public bool HasError => !string.IsNullOrEmpty(ErrorText);
    public bool HasRepository => _repo is not null;
    public bool HasNoRepository => _repo is null;
    public string RepositoryName => SelectedRepository?.Name ?? "Nenhum";
    public string WindowTitle => SelectedRepository is { } r ? $"{r.Name} — wye" : "wye";
    public bool IsClean => HasRepository && IsChangesTab && Files.Count == 0;

    public string FilesSummary => Files.Count switch
    {
        0 => "Nenhuma mudança",
        1 => "1 arquivo alterado",
        var n => $"{n} arquivos alterados",
    };
    public bool IsChangesTab => SelectedTabIndex == 0;
    public bool CanStageLines => IsChangesTab && !ShowStaged && _currentDiff.Hunks.Count > 0;
    public bool CanUnstageLines => IsChangesTab && ShowStaged && _currentDiff.Hunks.Count > 0;
    public string CommitButtonText => $"Fazer commit em {BranchName}";
    public string PushText => HasUpstream ? $"Push ↑{Ahead}" : "Publicar branch";
    public string PullText => $"Pull ↓{Behind}";
    public string PushLabel => HasUpstream ? "Push" : "Publicar branch";

    public string PushDetail => (HasUpstream, Ahead) switch
    {
        (false, _) => "Não está no remoto",
        (true, 0) => "Nada para enviar",
        (true, 1) => "1 commit para enviar",
        (true, var n) => $"{n} commits para enviar",
    };

    public string PullDetail => Behind switch
    {
        0 => "Nada para receber",
        1 => "1 commit para receber",
        var n => $"{n} commits para receber",
    };

    // ------------------------------------------------------------------ inicialização

    public async Task InitializeAsync()
    {
        try
        {
            _git = await GitLocator.CreateRunnerAsync();
            StatusText = $"Usando git em {_git.GitPath}";
        }
        catch (InvalidOperationException e)
        {
            ErrorText = e.Message;
            return;
        }

        foreach (var r in _store.GetAll())
        {
            Repositories.Add(r);
        }

        var lastPath = _store.GetSetting("last-repository");
        SelectedRepository = Repositories.FirstOrDefault(r => r.Path == lastPath) ?? Repositories.FirstOrDefault();
    }

    partial void OnSelectedRepositoryChanged(SavedRepository? value) => _ = OpenRepositoryAsync(value);

    private async Task OpenRepositoryAsync(SavedRepository? saved)
    {
        _watcher?.Dispose();
        _fetcher?.Dispose();
        _watcher = null;
        _fetcher = null;
        _repo = null;
        ClearDiff();
        Files.Clear();
        Commits.Clear();
        Branches.Clear();
        OnPropertyChanged(nameof(HasRepository));
        OnPropertyChanged(nameof(HasNoRepository));
        OnPropertyChanged(nameof(RepositoryName));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(IsClean));

        if (saved is null || _git is null)
        {
            return;
        }

        if (await GitRepository.FindRootAsync(_git, saved.Path) is null)
        {
            ErrorText = $"'{saved.Path}' não existe mais ou não é um repositório git.";
            return;
        }

        _repo = new GitRepository(_git, saved.Path);
        _store.Touch(saved.Id);
        _store.SetSetting("last-repository", saved.Path);
        OnPropertyChanged(nameof(HasRepository));
        OnPropertyChanged(nameof(HasNoRepository));
        OnPropertyChanged(nameof(RepositoryName));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(IsClean));

        _watcher = new RepositoryWatcher(saved.Path);
        _watcher.Changed += () => Dispatcher.UIThread.Post(() => _ = RefreshAsync());
        _fetcher = new BackgroundFetcher(async ct =>
        {
            var repo = _repo;
            if (repo is not null && (await repo.GetRemotesAsync(ct)).Count > 0)
            {
                await repo.FetchAsync(null, null, ct);
                await Dispatcher.UIThread.InvokeAsync(RefreshAsync);
            }
        });

        await RefreshAsync();
    }

    // ------------------------------------------------------------------ atualização

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var repo = _repo;
        if (repo is null)
        {
            return;
        }

        var generation = ++_refreshGeneration;
        try
        {
            var statusTask = repo.GetStatusAsync();
            var branchesTask = repo.GetBranchesAsync();
            var remotesTask = repo.GetRemotesAsync();
            await Task.WhenAll(statusTask, branchesTask, remotesTask);

            if (generation != _refreshGeneration || repo != _repo)
            {
                return; // chegou uma atualização mais nova
            }

            ApplyStatus(statusTask.Result);
            ApplyBranches(branchesTask.Result, statusTask.Result.Branch.Head);
            HasRemote = remotesTask.Result.Count > 0;

            if (SelectedTabIndex == 1)
            {
                await LoadHistoryAsync();
            }
        }
        catch (Exception e) when (e is GitException or IOException or InvalidOperationException)
        {
            ErrorText = e.Message;
        }
    }

    private void ApplyStatus(StatusResult status)
    {
        BranchName = status.Branch.Head ?? $"(HEAD destacado {status.Branch.Oid?[..7]})";
        Ahead = status.Branch.Ahead;
        Behind = status.Branch.Behind;
        HasUpstream = status.Branch.Upstream is not null;
        StagedCount = status.Files.Count(f => f.HasStagedChanges);
        OnPropertyChanged(nameof(CommitButtonText));
        OnPropertyChanged(nameof(PushText));
        OnPropertyChanged(nameof(PullText));
        OnPropertyChanged(nameof(PushLabel));
        OnPropertyChanged(nameof(PushDetail));
        OnPropertyChanged(nameof(PullDetail));

        var selectedPath = SelectedFile?.Path;
        Files.Clear();
        foreach (var f in status.Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            Files.Add(new FileItemViewModel(f));
        }

        // Mantém o arquivo selecionado e recarrega o diff dele (o conteúdo pode ter mudado).
        var again = Files.FirstOrDefault(f => f.Path == selectedPath);
        if (again is not null)
        {
            SelectedFile = again;
        }
        else if (SelectedTabIndex == 0)
        {
            SelectedFile = null;
            ClearDiff();
        }
    }

    private void ApplyBranches(IReadOnlyList<Branch> branches, string? currentHead)
    {
        _suppressBranchCheckout = true;
        try
        {
            Branches.Clear();
            var localNames = branches.Where(b => !b.IsRemote).Select(b => b.Name).ToHashSet();
            foreach (var b in branches.Where(b => !b.IsRemote))
            {
                Branches.Add(b);
            }

            // Remotas que ainda não têm branch local correspondente (como o Desktop faz na lista).
            foreach (var b in branches.Where(b => b.IsRemote && !localNames.Contains(b.LocalName)))
            {
                Branches.Add(b);
            }

            SelectedBranch = Branches.FirstOrDefault(b => !b.IsRemote && b.Name == currentHead);
        }
        finally
        {
            _suppressBranchCheckout = false;
        }
    }

    // ------------------------------------------------------------------ repositórios

    [RelayCommand]
    private async Task AddRepositoryAsync()
    {
        if (_git is null || PickFolder is null)
        {
            return;
        }

        var folder = await PickFolder();
        if (folder is null)
        {
            return;
        }

        var root = await GitRepository.FindRootAsync(_git, folder);
        if (root is null)
        {
            ErrorText = $"'{folder}' não é um repositório git. Use \"Criar repositório\" para inicializar um.";
            return;
        }

        AddAndSelect(root);
    }

    [RelayCommand]
    private async Task CreateRepositoryAsync()
    {
        if (_git is null || PickFolder is null)
        {
            return;
        }

        var folder = await PickFolder();
        if (folder is null)
        {
            return;
        }

        await RunAsync("Criando repositório…", async () =>
        {
            if (await GitRepository.FindRootAsync(_git, folder) is { } existing)
            {
                AddAndSelect(existing);
                return;
            }

            await GitRepository.InitAsync(_git, folder);
            AddAndSelect(folder);
        });
    }

    private void AddAndSelect(string path)
    {
        var saved = _store.Add(path);
        var existing = Repositories.FirstOrDefault(r => r.Id == saved.Id);
        if (existing is null)
        {
            existing = saved;
            var index = Repositories.TakeWhile(r => string.Compare(r.Name, saved.Name, StringComparison.OrdinalIgnoreCase) < 0).Count();
            Repositories.Insert(index, saved);
        }

        SelectedRepository = existing;
    }

    [RelayCommand]
    private void RemoveRepository()
    {
        if (SelectedRepository is not { } r)
        {
            return;
        }

        // Só tira da lista; nunca apaga arquivos do disco.
        _store.Remove(r.Id);
        Repositories.Remove(r);
        SelectedRepository = Repositories.FirstOrDefault();
    }

    // ------------------------------------------------------------------ branches

    partial void OnSelectedBranchChanged(Branch? value)
    {
        if (_suppressBranchCheckout || value is null || _repo is null || value.IsCurrent)
        {
            return;
        }

        _ = RunAsync($"Trocando para {value.Name}…", () => _repo.CheckoutAsync(value));
    }

    [RelayCommand]
    private Task CreateBranchAsync()
    {
        var name = NewBranchName.Trim();
        if (_repo is null || name.Length == 0)
        {
            return Task.CompletedTask;
        }

        return RunAsync($"Criando branch {name}…", async () =>
        {
            await _repo.CreateBranchAsync(name);
            NewBranchName = string.Empty;
        });
    }

    // ------------------------------------------------------------------ remotos

    [RelayCommand]
    private Task FetchAsync() =>
        _repo is null ? Task.CompletedTask : RunAsync("Fetch…", p => _repo.FetchAsync(null, p));

    [RelayCommand]
    private Task PullAsync() =>
        _repo is null ? Task.CompletedTask : RunAsync("Pull…", p => _repo.PullAsync(p));

    [RelayCommand]
    private Task PushAsync() =>
        _repo is null ? Task.CompletedTask : RunAsync("Push…", p => _repo.PushAsync(p));

    // ------------------------------------------------------------------ mudanças e stage

    [RelayCommand]
    private Task ToggleFileStageAsync(FileItemViewModel? item)
    {
        if (_repo is null || item is null)
        {
            return Task.CompletedTask;
        }

        return item.StageState == true
            ? RunAsync(null, () => _repo.UnstageFilesAsync([item.Path]))
            : RunAsync(null, () => _repo.StageFilesAsync([item.Path]));
    }

    [RelayCommand]
    private Task StageAllAsync() =>
        _repo is null || Files.Count == 0
            ? Task.CompletedTask
            : RunAsync(null, () => _repo.StageFilesAsync(Files.Select(f => f.Path)));

    [RelayCommand]
    private Task UnstageAllAsync()
    {
        var staged = Files.Where(f => f.Model.HasStagedChanges).Select(f => f.Path).ToList();
        return _repo is null || staged.Count == 0
            ? Task.CompletedTask
            : RunAsync(null, () => _repo.UnstageFilesAsync(staged));
    }

    [RelayCommand]
    private Task StageSelectedLinesAsync()
    {
        var lines = SelectedLineIndexes();
        return _repo is null || lines.Count == 0
            ? Task.CompletedTask
            : RunAsync("Preparando linhas…", () => _repo.StageLinesAsync(_currentDiff, lines));
    }

    [RelayCommand]
    private Task UnstageSelectedLinesAsync()
    {
        var lines = SelectedLineIndexes();
        return _repo is null || lines.Count == 0
            ? Task.CompletedTask
            : RunAsync("Removendo linhas do stage…", () => _repo.UnstageLinesAsync(_currentDiff, lines));
    }

    private HashSet<int> SelectedLineIndexes() =>
        DiffLines.Where(l => l.IsSelected && l.IsChange).Select(l => l.Line.Index).ToHashSet();

    private bool CanCommit() => StagedCount > 0 && CommitSummary.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync()
    {
        if (_repo is null)
        {
            return;
        }

        var message = CommitSummary.Trim();
        if (CommitDescription.Trim().Length > 0)
        {
            message += "\n\n" + CommitDescription.Trim();
        }

        string? sha = null;
        await RunAsync("Fazendo commit…", async () =>
        {
            sha = await _repo.CommitAsync(message);
            CommitSummary = string.Empty;
            CommitDescription = string.Empty;
        });

        if (sha is not null)
        {
            StatusText = $"Commit {sha[..7]} criado em {BranchName}";
        }
    }

    // ------------------------------------------------------------------ diff

    partial void OnSelectedFileChanged(FileItemViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        // Ao escolher um arquivo, mostra o lado que tem mudanças (como o Desktop, que mostra o diff "útil").
        if (value.StageState == true && !ShowStaged)
        {
            ShowStaged = true; // dispara o carregamento
            return;
        }

        if (value.StageState == false && ShowStaged)
        {
            ShowStaged = false;
            return;
        }

        _ = LoadWorkingDiffAsync();
    }

    partial void OnShowStagedChanged(bool value) => _ = LoadWorkingDiffAsync();

    private async Task LoadWorkingDiffAsync()
    {
        if (_repo is null || SelectedFile is not { } file || SelectedTabIndex != 0)
        {
            return;
        }

        var gen = ++_diffGeneration;
        try
        {
            var diff = await _repo.GetWorkingDiffAsync(file.Model, ShowStaged);
            if (gen == _diffGeneration)
            {
                ShowDiff(file.Path + (ShowStaged ? "  (preparado)" : "  (não preparado)"), diff);
            }
        }
        catch (GitException e)
        {
            ErrorText = e.Message;
        }
    }

    private void ShowDiff(string title, FileDiff diff)
    {
        _currentDiff = diff;
        DiffTitle = title;
        DiffLines.Clear();
        DiffMessage = diff switch
        {
            { IsTooLarge: true } => "Diff grande demais para exibir (> 70 MB).",
            { IsBinary: true } => "Arquivo binário.",
            { Hunks.Count: 0 } => "Sem mudanças deste lado. Alterne entre \"Não preparadas\" e \"Preparadas\".",
            _ => null,
        };

        foreach (var line in diff.Lines)
        {
            DiffLines.Add(new DiffLineViewModel(line));
        }

        OnPropertyChanged(nameof(CanStageLines));
        OnPropertyChanged(nameof(CanUnstageLines));
    }

    private void ClearDiff()
    {
        _diffGeneration++;
        _currentDiff = FileDiff.Empty;
        DiffLines.Clear();
        DiffTitle = null;
        DiffMessage = null;
        OnPropertyChanged(nameof(CanStageLines));
        OnPropertyChanged(nameof(CanUnstageLines));
    }

    // ------------------------------------------------------------------ histórico

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsChangesTab));
        OnPropertyChanged(nameof(IsClean));
        ClearDiff();
        if (value == 1)
        {
            _ = LoadHistoryAsync();
        }
        else
        {
            _ = LoadWorkingDiffAsync();
        }
    }

    private async Task LoadHistoryAsync()
    {
        if (_repo is null)
        {
            return;
        }

        try
        {
            var selectedSha = SelectedCommit?.Model.Sha;
            var commits = await _repo.GetCommitsAsync(limit: 200);
            Commits.Clear();
            foreach (var c in commits)
            {
                Commits.Add(new CommitItemViewModel(c));
            }

            SelectedCommit = Commits.FirstOrDefault(c => c.Model.Sha == selectedSha) ?? Commits.FirstOrDefault();
        }
        catch (GitException e)
        {
            ErrorText = e.Message;
        }
    }

    partial void OnSelectedCommitChanged(CommitItemViewModel? value) => _ = LoadCommitFilesAsync(value);

    private async Task LoadCommitFilesAsync(CommitItemViewModel? commit)
    {
        CommitFiles.Clear();
        ClearDiff();
        if (_repo is null || commit is null)
        {
            return;
        }

        try
        {
            foreach (var f in await _repo.GetCommitFilesAsync(commit.Model.Sha))
            {
                CommitFiles.Add(new CommitFileItemViewModel(f));
            }

            SelectedCommitFile = CommitFiles.FirstOrDefault();
        }
        catch (GitException e)
        {
            ErrorText = e.Message;
        }
    }

    partial void OnSelectedCommitFileChanged(CommitFileItemViewModel? value) => _ = LoadCommitDiffAsync(value);

    private async Task LoadCommitDiffAsync(CommitFileItemViewModel? file)
    {
        if (_repo is null || file is null || SelectedCommit is not { } commit)
        {
            return;
        }

        var gen = ++_diffGeneration;
        try
        {
            var diff = await _repo.GetCommitFileDiffAsync(commit.Model.Sha, file.Model);
            if (gen == _diffGeneration)
            {
                ShowDiff($"{file.Path}  @ {commit.ShortSha}", diff);
            }
        }
        catch (GitException e)
        {
            ErrorText = e.Message;
        }
    }

    // ------------------------------------------------------------------ infraestrutura

    [RelayCommand]
    private void DismissError() => ErrorText = null;

    private Task RunAsync(string? status, Func<Task> action) => RunAsync(status, _ => action());

    /// <summary>Executa uma operação git mostrando progresso e erro, e atualiza a tela no final.</summary>
    private async Task RunAsync(string? status, Func<IProgress<string>, Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorText = null;
        if (status is not null)
        {
            StatusText = status;
        }

        var progress = new Progress<string>(line => StatusText = line);
        try
        {
            await action(progress);
            if (status is not null)
            {
                StatusText = "Pronto";
            }
        }
        catch (Exception e) when (e is GitException or InvalidOperationException or IOException)
        {
            ErrorText = e.Message;
            StatusText = "Falhou";
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _fetcher?.Dispose();
    }
}
