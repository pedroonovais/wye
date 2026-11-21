namespace Wye.App.Services;

/// <summary>
/// Observa o working tree e dispara <see cref="Changed"/> com debounce.
/// Melhoria em relação ao GitHub Desktop, que atualiza o status principalmente quando a janela
/// ganha foco e após cada operação: aqui mudanças feitas por editores/terminal aparecem sozinhas.
/// </summary>
public sealed class RepositoryWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _timer;
    private readonly string _gitDir;

    public event Action? Changed;

    public RepositoryWatcher(string repositoryPath)
    {
        _gitDir = Path.Combine(repositoryPath, ".git");
        _timer = new Timer(_ => Changed?.Invoke(), null, Timeout.Infinite, Timeout.Infinite);

        try
        {
            _watcher = new FileSystemWatcher(repositoryPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Changed += OnEvent;
            _watcher.Created += OnEvent;
            _watcher.Deleted += OnEvent;
            _watcher.Renamed += OnEvent;
            _watcher.Error += (_, _) => Schedule(); // buffer estourou: melhor atualizar tudo
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException)
        {
            // Ex.: limite de inotify do Linux atingido. O app continua funcionando com atualização manual.
            _watcher = null;
        }
    }

    public bool IsActive => _watcher is not null;

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        var path = e.FullPath;
        if (path.StartsWith(_gitDir, StringComparison.Ordinal))
        {
            // Dentro de .git só interessam HEAD, index e refs (troca de branch, commit feito fora do app).
            var rel = path[_gitDir.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var relevant = rel == "HEAD" || rel == "index" || rel.StartsWith("refs", StringComparison.Ordinal);
            if (!relevant || rel.EndsWith(".lock", StringComparison.Ordinal))
            {
                return;
            }
        }

        Schedule();
    }

    private void Schedule() => _timer.Change(Debounce, Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        _watcher?.Dispose();
        _timer.Dispose();
    }
}
