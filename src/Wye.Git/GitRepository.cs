using Wye.Core.Models;
using Wye.Core.Parsing;

namespace Wye.Git;

/// <summary>
/// Operações git de um repositório. Cada método corresponde a um módulo de app/src/lib/git/*.ts
/// do GitHub Desktop e usa os mesmos argumentos (citados nos comentários).
/// </summary>
public sealed class GitRepository(GitRunner git, string path)
{
    /// <summary>Limite rígido de diff do Desktop (diff.ts:45: MaxDiffBufferSize = 70e6).</summary>
    public const long MaxDiffSize = 70_000_000;

    public string Path { get; } = path;
    public GitRunner Git { get; } = git;

    private Task<GitResult> Run(IReadOnlyList<string> args, GitRunOptions? o = null, CancellationToken ct = default) =>
        Git.RunAsync(Path, args, o, ct);

    // ------------------------------------------------------------------ repositório

    /// <summary>Devolve a raiz do repositório que contém <paramref name="anyPath"/>, ou null se não for um repo.</summary>
    public static async Task<string?> FindRootAsync(GitRunner git, string anyPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(anyPath))
        {
            return null;
        }

        var r = await git.RunAsync(anyPath, ["rev-parse", "--show-toplevel"],
            new GitRunOptions { SuccessExitCodes = new HashSet<int> { 0, 128 } }, ct);
        return r.ExitCode == 0 ? System.IO.Path.GetFullPath(r.Stdout.Trim()) : null;
    }

    public static async Task<GitRepository> InitAsync(GitRunner git, string path, string defaultBranch = "main", CancellationToken ct = default)
    {
        Directory.CreateDirectory(path);
        await git.RunAsync(path, ["init", "-b", defaultBranch], null, ct);
        return new GitRepository(git, path);
    }

    public static async Task<GitRepository> CloneAsync(
        GitRunner git, string url, string destination, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var parent = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destination))!;
        Directory.CreateDirectory(parent);
        await git.RunAsync(parent, ["clone", "--recursive", "--progress", "--", url, destination],
            new GitRunOptions { Progress = progress }, ct);
        return new GitRepository(git, destination);
    }

    // ------------------------------------------------------------------ status (status.ts:195-200)

    public async Task<StatusResult> GetStatusAsync(CancellationToken ct = default)
    {
        var r = await Run(
            ["--no-optional-locks", "status", "--untracked-files=all", "--branch", "--porcelain=2", "-z"],
            null, ct);
        return StatusParser.Parse(r.Stdout);
    }

    // ------------------------------------------------------------------ histórico (log.ts:120-150)

    public async Task<IReadOnlyList<Commit>> GetCommitsAsync(
        string? revisionRange = null, int limit = 100, int skip = 0, CancellationToken ct = default)
    {
        var args = new List<string> { "log" };
        if (revisionRange is not null)
        {
            args.Add(revisionRange);
        }

        args.Add($"--max-count={limit}");
        if (skip > 0)
        {
            args.Add($"--skip={skip}");
        }

        args.AddRange(LogParser.FormatArgs);
        args.Add("--");

        // Repositório sem nenhum commit: `git log` sai com 128.
        var r = await Run(args, new GitRunOptions { SuccessExitCodes = new HashSet<int> { 0, 128 } }, ct);
        return r.ExitCode == 0 ? LogParser.Parse(r.Stdout) : [];
    }

    /// <summary>Arquivos alterados por um commit (`git show --name-status -z`).</summary>
    public async Task<IReadOnlyList<CommittedFileChange>> GetCommitFilesAsync(string sha, CancellationToken ct = default)
    {
        var r = await Run(
            ["show", "--no-ext-diff", "--format=", "--name-status", "-z", "-M", "--first-parent", sha, "--"],
            null, ct);
        var tokens = r.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var files = new List<CommittedFileChange>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var status = tokens[i].Trim('\n');
            if (status.Length == 0)
            {
                continue;
            }

            var code = status[0];
            if (code is 'R' or 'C')
            {
                var oldPath = tokens[++i];
                var newPath = tokens[++i];
                files.Add(new CommittedFileChange(newPath, oldPath, StatusParser.KindFromNameStatus(code), code));
            }
            else
            {
                files.Add(new CommittedFileChange(tokens[++i], null, StatusParser.KindFromNameStatus(code), code));
            }
        }

        return files;
    }

    // ------------------------------------------------------------------ diff (diff.ts:342-349)

    /// <summary>
    /// Diff de um arquivo no working tree. <paramref name="staged"/>=false: index → working tree;
    /// true: HEAD → index. Arquivos não rastreados são comparados com /dev/null, como o Desktop faz.
    /// </summary>
    public async Task<FileDiff> GetWorkingDiffAsync(FileChange file, bool staged, CancellationToken ct = default)
    {
        List<string> args;
        var successCodes = new HashSet<int> { 0 };

        if (file.IsUntracked)
        {
            if (staged)
            {
                return FileDiff.Empty;
            }

            // --no-index sai com 1 quando há diferença.
            args = ["diff", "--no-ext-diff", "--no-color", "--no-index", "--", "/dev/null", file.Path];
            successCodes.Add(1);
        }
        else
        {
            args = ["diff", "--no-ext-diff", "--no-color"];
            if (staged)
            {
                args.Add("--cached");
            }

            args.Add("--");
            if (file.OldPath is not null && staged)
            {
                // Renomeação no index: passar os dois caminhos permite ao git detectar o rename.
                args.Insert(args.Count - 1, "-M");
                args.Add(file.OldPath);
            }

            args.Add(file.Path);
        }

        return await RunDiffAsync(args, successCodes, ct);
    }

    public Task<FileDiff> GetCommitFileDiffAsync(string sha, CommittedFileChange file, CancellationToken ct = default)
    {
        List<string> args = ["show", "--no-ext-diff", "--no-color", "--format=", "-M", "--first-parent", sha, "--"];
        if (file.OldPath is not null)
        {
            args.Add(file.OldPath);
        }

        args.Add(file.Path);
        return RunDiffAsync(args, new HashSet<int> { 0 }, ct);
    }

    private async Task<FileDiff> RunDiffAsync(List<string> args, HashSet<int> successCodes, CancellationToken ct)
    {
        try
        {
            var r = await Run(args, new GitRunOptions { SuccessExitCodes = successCodes, MaxOutputBytes = MaxDiffSize }, ct);
            return DiffParser.Parse(r.Stdout);
        }
        catch (GitOutputTooLargeException)
        {
            return FileDiff.TooLarge;
        }
    }

    // ------------------------------------------------------------------ stage / unstage

    public Task StageFilesAsync(IEnumerable<string> paths, CancellationToken ct = default) =>
        Run(["add", "--all", "--", .. paths], null, ct);

    public async Task UnstageFilesAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var list = paths.ToList();
        var status = await GetStatusAsync(ct);
        if (status.Branch.IsUnborn)
        {
            // Sem HEAD não há para onde "resetar": só tiramos do index.
            await Run(["rm", "--cached", "-r", "-q", "--", .. list], null, ct);
        }
        else
        {
            await Run(["reset", "-q", "HEAD", "--", .. list], null, ct);
        }
    }

    /// <summary>Aplica um patch ao index (apply.ts:54-82: `git apply --cached --unidiff-zero`).</summary>
    public Task ApplyPatchToIndexAsync(string patch, bool reverse, CancellationToken ct = default)
    {
        List<string> args = ["apply", "--cached", "--unidiff-zero", "--whitespace=nowarn"];
        if (reverse)
        {
            args.Add("--reverse");
        }

        args.Add("-");
        return Run(args, new GitRunOptions { Stdin = patch }, ct);
    }

    /// <summary>
    /// Prepara (stage) só as linhas selecionadas do diff index → working tree.
    /// Para arquivo não rastreado o diff é /dev/null → arquivo; o patch "new file" com só parte
    /// das linhas cria o arquivo no index com esse conteúdo parcial.
    /// </summary>
    public async Task StageLinesAsync(FileDiff unstagedDiff, IReadOnlySet<int> lines, CancellationToken ct = default)
    {
        var patch = PatchBuilder.Build(unstagedDiff, lines, PatchDirection.Forward);
        if (patch is not null)
        {
            await ApplyPatchToIndexAsync(patch, reverse: false, ct);
        }
    }

    /// <summary>Retira do stage só as linhas selecionadas do diff HEAD → index.</summary>
    public async Task UnstageLinesAsync(FileDiff stagedDiff, IReadOnlySet<int> lines, CancellationToken ct = default)
    {
        var patch = PatchBuilder.Build(stagedDiff, lines, PatchDirection.Reverse);
        if (patch is not null)
        {
            await ApplyPatchToIndexAsync(patch, reverse: true, ct);
        }
    }

    // ------------------------------------------------------------------ commit (commit.ts:28-39)

    public async Task<string> CommitAsync(string message, bool amend = false, CancellationToken ct = default)
    {
        List<string> args = ["commit", "-F", "-", "--cleanup=strip"];
        if (amend)
        {
            args.Add("--amend");
        }

        await Run(args, new GitRunOptions { Stdin = message }, ct);
        var head = await Run(["rev-parse", "HEAD"], null, ct);
        return head.Stdout.Trim();
    }

    // ------------------------------------------------------------------ branches (for-each-ref.ts)

    public async Task<IReadOnlyList<Branch>> GetBranchesAsync(CancellationToken ct = default)
    {
        var r = await Run(
        [
            "for-each-ref",
            "--format=%(refname)%00%(refname:short)%00%(upstream:short)%00%(objectname)%00%(HEAD)%00%(symref)",
            "refs/heads", "refs/remotes",
        ], null, ct);

        var branches = new List<Branch>();
        foreach (var line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('\0');
            if (f.Length < 6 || f[5].Length > 0)
            {
                continue; // ignora refs simbólicas como origin/HEAD
            }

            var isRemote = f[0].StartsWith("refs/remotes/", StringComparison.Ordinal);
            branches.Add(new Branch(f[1], f[0], f[2].Length > 0 ? f[2] : null, f[3], f[4] == "*", isRemote));
        }

        return branches;
    }

    public Task CreateBranchAsync(string name, string? startPoint = null, bool checkout = true, CancellationToken ct = default)
    {
        List<string> args = checkout ? ["checkout", "-b", name] : ["branch", name];
        if (startPoint is not null)
        {
            args.Add(startPoint);
        }

        return Run(args, null, ct);
    }

    public Task CheckoutAsync(Branch branch, CancellationToken ct = default)
    {
        if (!branch.IsRemote)
        {
            return Run(["checkout", branch.Name, "--"], null, ct);
        }

        // Branch remota: cria a local rastreando a remota, como o Desktop faz ao escolher "origin/x".
        return Run(["checkout", "-b", branch.LocalName, "--track", branch.Name, "--"], null, ct);
    }

    public Task DeleteBranchAsync(string name, bool force = false, CancellationToken ct = default) =>
        Run(["branch", force ? "-D" : "-d", name], null, ct);

    // ------------------------------------------------------------------ remotos (fetch.ts / pull.ts / push.ts)

    public async Task<IReadOnlyList<Remote>> GetRemotesAsync(CancellationToken ct = default)
    {
        var r = await Run(["remote", "-v"], null, ct);
        var map = new Dictionary<string, (string Fetch, string Push)>();
        foreach (var line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            map.TryGetValue(parts[0], out var cur);
            map[parts[0]] = parts[2] == "(push)" ? (cur.Fetch, parts[1]) : (parts[1], cur.Push);
        }

        return map.Select(kv => new Remote(kv.Key, kv.Value.Fetch, kv.Value.Push ?? kv.Value.Fetch)).ToList();
    }

    public Task AddRemoteAsync(string name, string url, CancellationToken ct = default) =>
        Run(["remote", "add", name, url], null, ct);

    /// <summary>fetch.ts:15-17 — `git fetch --progress --prune`.</summary>
    public Task FetchAsync(string? remote = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        List<string> args = ["fetch", "--progress", "--prune"];
        args.Add(remote ?? "--all");
        return Run(args, new GitRunOptions { Progress = progress }, ct);
    }

    /// <summary>
    /// Pull respeitando `pull.rebase` do usuário; se não houver configuração, faz merge
    /// (o Desktop passa --no-rebase nesse caso para evitar o erro "divergent branches" do git ≥ 2.33).
    /// </summary>
    public async Task PullAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var cfg = await Run(["config", "--get", "pull.rebase"],
            new GitRunOptions { SuccessExitCodes = new HashSet<int> { 0, 1 } }, ct);
        List<string> args = ["pull", "--progress"];
        if (cfg.ExitCode != 0)
        {
            args.Add("--no-rebase");
        }

        await Run(args, new GitRunOptions { Progress = progress }, ct);
    }

    /// <summary>push.ts:58-77 — publica a branch com --set-upstream se ainda não tiver upstream.</summary>
    public async Task PushAsync(IProgress<string>? progress = null, bool forceWithLease = false, CancellationToken ct = default)
    {
        var status = await GetStatusAsync(ct);
        var branch = status.Branch.Head ?? throw new InvalidOperationException("HEAD destacado: crie uma branch antes de enviar.");

        List<string> args = ["push", "--progress"];
        if (forceWithLease)
        {
            args.Add("--force-with-lease");
        }

        if (status.Branch.Upstream is null)
        {
            var remotes = await GetRemotesAsync(ct);
            var remote = remotes.FirstOrDefault(r => r.Name == "origin") ?? remotes.FirstOrDefault()
                ?? throw new InvalidOperationException("Repositório sem remoto configurado.");
            args.AddRange(["--set-upstream", remote.Name, $"refs/heads/{branch}:refs/heads/{branch}"]);
        }

        await Run(args, new GitRunOptions { Progress = progress }, ct);
    }
}
