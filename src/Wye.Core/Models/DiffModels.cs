namespace Wye.Core.Models;

public enum DiffLineType
{
    Context,
    Add,
    Delete,
    HunkHeader,
    NoNewlineMarker,
}

/// <summary>
/// Uma linha do diff. <see cref="Index"/> é a posição absoluta da linha dentro de
/// <see cref="FileDiff.Lines"/> e é o identificador usado na seleção para stage parcial.
/// </summary>
public sealed record DiffLine(
    int Index,
    DiffLineType Type,
    string Text,
    int? OldLineNumber,
    int? NewLineNumber,
    int HunkIndex)
{
    public bool IsChange => Type is DiffLineType.Add or DiffLineType.Delete;
}

public sealed record DiffHunk(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    string Header,
    int FirstLineIndex,
    int LastLineIndex);

public sealed record FileDiff(
    string? OldPath,
    string? NewPath,
    IReadOnlyList<string> HeaderLines,
    IReadOnlyList<DiffHunk> Hunks,
    IReadOnlyList<DiffLine> Lines,
    bool IsBinary,
    bool IsTooLarge = false)
{
    public static FileDiff Empty { get; } = new(null, null, [], [], [], false);
    public static FileDiff TooLarge { get; } = new(null, null, [], [], [], false, true);
    public bool HasChanges => IsBinary || Hunks.Count > 0;
}
