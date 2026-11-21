using Wye.Core.Models;

namespace Wye.Git.Tests;

/// <summary>Testes de integração: rodam o git de verdade em repositórios temporários.</summary>
public sealed class GitRepositoryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wye-tests", Guid.NewGuid().ToString("N"));
    private GitRunner _git = null!;
    private GitRepository _repo = null!;

    public async Task InitializeAsync()
    {
        _git = await GitLocator.CreateRunnerAsync();
        _repo = await GitRepository.InitAsync(_git, Path.Combine(_root, "work"));
        await ConfigureAsync(_repo.Path);
    }

    public Task DisposeAsync()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal); // objetos do git são somente leitura no Windows
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    private async Task ConfigureAsync(string path)
    {
        // Isola o teste da configuração global de quem roda (assinatura GPG, hooks etc.).
        foreach (var (k, v) in new[]
                 {
                     ("user.name", "Test"), ("user.email", "test@example.com"),
                     ("commit.gpgsign", "false"), ("core.autocrlf", "false"), ("core.hooksPath", "/dev/null"),
                 })
        {
            await _git.RunAsync(path, ["config", k, v]);
        }
    }

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(_repo.Path, name), content.ReplaceLineEndings("\n"));

    private string Read(string name) => File.ReadAllText(Path.Combine(_repo.Path, name));

    private async Task<string> IndexContent(string name) =>
        (await _git.RunAsync(_repo.Path, ["show", $":{name}"])).Stdout;

    [Fact]
    public async Task Status_reports_unborn_branch_and_untracked_file()
    {
        Write("a.txt", "hello\n");

        var status = await _repo.GetStatusAsync();

        Assert.True(status.Branch.IsUnborn);
        Assert.Equal("main", status.Branch.Head);
        var file = Assert.Single(status.Files);
        Assert.Equal(ChangeKind.Untracked, file.Kind);
    }

    [Fact]
    public async Task Stage_commit_and_read_history()
    {
        Write("a.txt", "hello\n");
        await _repo.StageFilesAsync(["a.txt"]);
        var sha = await _repo.CommitAsync("Primeiro commit\n\nCorpo com várias\nlinhas\n# comentário removido");

        var commits = await _repo.GetCommitsAsync();

        var c = Assert.Single(commits);
        Assert.Equal(sha, c.Sha);
        Assert.Equal("Primeiro commit", c.Summary);
        Assert.Equal("Corpo com várias\nlinhas", c.Body); // --cleanup=strip removeu o comentário
        Assert.Empty((await _repo.GetStatusAsync()).Files);

        var files = await _repo.GetCommitFilesAsync(sha);
        Assert.Equal(ChangeKind.Added, Assert.Single(files).Kind);
        var diff = await _repo.GetCommitFileDiffAsync(sha, files[0]);
        Assert.Equal("hello", Assert.Single(diff.Lines, l => l.Type == DiffLineType.Add).Text);
    }

    [Fact]
    public async Task Partial_stage_and_unstage_of_lines()
    {
        Write("f.txt", "one\ntwo\nthree\nfour\n");
        await _repo.StageFilesAsync(["f.txt"]);
        await _repo.CommitAsync("base");

        Write("f.txt", "one\nTWO\nthree\nFOUR\n");
        var file = Assert.Single((await _repo.GetStatusAsync()).Files);
        var diff = await _repo.GetWorkingDiffAsync(file, staged: false);
        var selected = diff.Lines.Where(l => l.Text is "two" or "TWO").Select(l => l.Index).ToHashSet();

        await _repo.StageLinesAsync(diff, selected);

        Assert.Equal("one\nTWO\nthree\nfour\n", await IndexContent("f.txt"));
        Assert.Equal("one\nTWO\nthree\nFOUR\n", Read("f.txt")); // working tree intacto

        file = Assert.Single((await _repo.GetStatusAsync()).Files);
        Assert.True(file.HasStagedChanges);
        Assert.True(file.HasUnstagedChanges);

        // Agora retira só a adição "TWO" do stage (fica a remoção de "two").
        var staged = await _repo.GetWorkingDiffAsync(file, staged: true);
        var twoAdd = staged.Lines.Where(l => l is { Type: DiffLineType.Add, Text: "TWO" }).Select(l => l.Index).ToHashSet();
        await _repo.UnstageLinesAsync(staged, twoAdd);

        Assert.Equal("one\nthree\nfour\n", await IndexContent("f.txt"));
    }

    [Fact]
    public async Task Partial_stage_of_untracked_file()
    {
        Write("seed.txt", "x\n");
        await _repo.StageFilesAsync(["seed.txt"]);
        await _repo.CommitAsync("seed");

        Write("new.txt", "a\nb\nc\n");
        var file = (await _repo.GetStatusAsync()).Files.Single(f => f.Path == "new.txt");
        var diff = await _repo.GetWorkingDiffAsync(file, staged: false);
        var lines = diff.Lines.Where(l => l.Text is "a" or "c").Select(l => l.Index).ToHashSet();

        await _repo.StageLinesAsync(diff, lines);

        Assert.Equal("a\nc\n", await IndexContent("new.txt"));
    }

    [Fact]
    public async Task Unstage_works_on_unborn_branch()
    {
        Write("a.txt", "x\n");
        await _repo.StageFilesAsync(["a.txt"]);
        await _repo.UnstageFilesAsync(["a.txt"]);

        Assert.Equal(ChangeKind.Untracked, Assert.Single((await _repo.GetStatusAsync()).Files).Kind);
    }

    [Fact]
    public async Task Branches_push_fetch_and_pull_with_local_remote()
    {
        Write("a.txt", "1\n");
        await _repo.StageFilesAsync(["a.txt"]);
        await _repo.CommitAsync("c1");

        var bare = Path.Combine(_root, "remote.git");
        await _git.RunAsync(_root, ["init", "--bare", "-b", "main", bare]);
        await _repo.AddRemoteAsync("origin", bare);

        var progress = new List<string>();
        await _repo.PushAsync(new Progress<string>(progress.Add));
        var status = await _repo.GetStatusAsync();
        Assert.Equal("origin/main", status.Branch.Upstream);

        // Segundo clone empurra um commit; o primeiro faz fetch e pull.
        var other = await GitRepository.CloneAsync(_git, bare, Path.Combine(_root, "other"));
        await ConfigureAsync(other.Path);
        File.WriteAllText(Path.Combine(other.Path, "b.txt"), "2\n");
        await other.StageFilesAsync(["b.txt"]);
        await other.CommitAsync("c2");
        await other.PushAsync();

        await _repo.FetchAsync("origin");
        Assert.Equal(1, (await _repo.GetStatusAsync()).Branch.Behind);
        await _repo.PullAsync();
        Assert.Equal(2, (await _repo.GetCommitsAsync()).Count);

        await _repo.CreateBranchAsync("feature");
        var branches = await _repo.GetBranchesAsync();
        Assert.Contains(branches, b => b is { Name: "feature", IsCurrent: true });
        Assert.Contains(branches, b => b is { Name: "origin/main", IsRemote: true });
        Assert.DoesNotContain(branches, b => b.Name == "origin/HEAD" || b.Name == "origin");

        await _repo.CheckoutAsync(branches.Single(b => b.Name == "main"));
        await _repo.DeleteBranchAsync("feature");
        Assert.DoesNotContain(await _repo.GetBranchesAsync(), b => b.Name == "feature");

        // Escolher uma branch que só existe no remoto cria a local rastreando a remota.
        await other.CreateBranchAsync("topic");
        await other.PushAsync();
        await _repo.FetchAsync("origin");
        var remoteTopic = (await _repo.GetBranchesAsync()).Single(b => b.Name == "origin/topic");
        await _repo.CheckoutAsync(remoteTopic);
        var topicStatus = await _repo.GetStatusAsync();
        Assert.Equal("topic", topicStatus.Branch.Head);
        Assert.Equal("origin/topic", topicStatus.Branch.Upstream);
    }

    [Fact]
    public async Task Find_root_returns_null_outside_repo()
    {
        var outside = Path.Combine(_root, "not-a-repo");
        Directory.CreateDirectory(outside);
        Assert.Null(await GitRepository.FindRootAsync(_git, outside));

        var sub = Path.Combine(_repo.Path, "sub");
        Directory.CreateDirectory(sub);
        Assert.Equal(Path.GetFullPath(_repo.Path), await GitRepository.FindRootAsync(_git, sub));
    }
}
