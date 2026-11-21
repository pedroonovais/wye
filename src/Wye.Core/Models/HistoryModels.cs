namespace Wye.Core.Models;

public sealed record GitIdentity(string Name, string Email, DateTimeOffset Date);

public sealed record Commit(
    string Sha,
    string ShortSha,
    string Summary,
    string Body,
    GitIdentity Author,
    GitIdentity Committer,
    IReadOnlyList<string> Parents,
    IReadOnlyList<string> Refs)
{
    public bool IsMerge => Parents.Count > 1;
}

public sealed record Branch(
    string Name,
    string FullRef,
    string? Upstream,
    string Sha,
    bool IsCurrent,
    bool IsRemote)
{
    /// <summary>Para "origin/feature" devolve "feature"; para branches locais devolve o próprio nome.</summary>
    public string LocalName => IsRemote && Name.IndexOf('/') is var i and > 0 ? Name[(i + 1)..] : Name;
}

public sealed record Remote(string Name, string FetchUrl, string PushUrl);

/// <summary>Arquivo alterado em um commit (saída de `git show --name-status -z`).</summary>
public sealed record CommittedFileChange(string Path, string? OldPath, ChangeKind Kind, char Code);
