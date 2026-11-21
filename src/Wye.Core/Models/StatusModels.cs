namespace Wye.Core.Models;

/// <summary>Tipo de mudança de um arquivo, derivado dos códigos XY do `git status --porcelain=2`.</summary>
public enum ChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Untracked,
    Conflicted,
}

/// <summary>
/// Uma entrada do status. <see cref="IndexStatus"/> é o "X" (index vs HEAD) e
/// <see cref="WorkTreeStatus"/> é o "Y" (working tree vs index), como no porcelain do git.
/// '.' significa "sem mudança" nesse lado.
/// </summary>
public sealed record FileChange(
    string Path,
    string? OldPath,
    char IndexStatus,
    char WorkTreeStatus,
    ChangeKind Kind)
{
    public bool IsUntracked => Kind == ChangeKind.Untracked;
    public bool IsConflicted => Kind == ChangeKind.Conflicted;

    /// <summary>Há algo no index (staged) para este arquivo.</summary>
    public bool HasStagedChanges => !IsUntracked && !IsConflicted && IndexStatus != '.';

    /// <summary>Há algo no working tree que ainda não foi para o index.</summary>
    public bool HasUnstagedChanges => IsUntracked || IsConflicted || WorkTreeStatus != '.';

    /// <summary>Letra curta para exibição (M, A, D, R, C, T, ?, U).</summary>
    public char DisplayCode => Kind switch
    {
        ChangeKind.Modified => 'M',
        ChangeKind.Added => 'A',
        ChangeKind.Deleted => 'D',
        ChangeKind.Renamed => 'R',
        ChangeKind.Copied => 'C',
        ChangeKind.TypeChanged => 'T',
        ChangeKind.Untracked => '?',
        ChangeKind.Conflicted => 'U',
        _ => ' ',
    };
}

/// <summary>Informações do cabeçalho "# branch.*" do porcelain v2.</summary>
public sealed record BranchStatus(
    string? Head,
    string? Oid,
    string? Upstream,
    int Ahead,
    int Behind)
{
    public bool IsDetached => Head is null;
    public bool IsUnborn => Oid is null;
    public static BranchStatus Empty { get; } = new(null, null, null, 0, 0);
}

public sealed record StatusResult(BranchStatus Branch, IReadOnlyList<FileChange> Files);
