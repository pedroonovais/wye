using CommunityToolkit.Mvvm.ComponentModel;
using Wye.Core.Models;

namespace Wye.App.ViewModels;

/// <summary>
/// Letra e cor do status de um arquivo, como no manual da marca: A (novo), M (modificado),
/// R (renomeado), D (removido) e ! (em conflito). A letra sempre aparece, a cor nunca é o único sinal.
/// </summary>
public abstract class StatusItemViewModel(string path, ChangeKind kind, char code)
{
    public string Path { get; } = path;
    public ChangeKind ChangeKind { get; } = kind;

    public string FileName => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? string.Empty;
    public bool HasFolder => Folder.Length > 0;

    public string Code => ChangeKind switch
    {
        ChangeKind.Untracked => "A",
        ChangeKind.Conflicted => "!",
        _ => code.ToString(),
    };

    public string Kind => ChangeKind switch
    {
        ChangeKind.Modified => "Modificado",
        ChangeKind.Added => "Novo",
        ChangeKind.Untracked => "Novo, ainda fora do git",
        ChangeKind.Deleted => "Removido",
        ChangeKind.Renamed => "Renomeado",
        ChangeKind.Copied => "Copiado",
        ChangeKind.TypeChanged => "Tipo alterado",
        ChangeKind.Conflicted => "Em conflito",
        _ => string.Empty,
    };

    public bool IsAdded => ChangeKind is ChangeKind.Added or ChangeKind.Untracked or ChangeKind.Copied;
    public bool IsModified => ChangeKind is ChangeKind.Modified or ChangeKind.TypeChanged;
    public bool IsRenamed => ChangeKind == ChangeKind.Renamed;
    public bool IsDeleted => ChangeKind == ChangeKind.Deleted;
    public bool IsConflict => ChangeKind == ChangeKind.Conflicted;
}

public sealed class FileItemViewModel(FileChange model)
    : StatusItemViewModel(model.Path, model.Kind, model.DisplayCode)
{
    public FileChange Model { get; } = model;

    public string Tooltip => Model.OldPath is null
        ? $"{Model.Path} ({Kind.ToLowerInvariant()})"
        : $"{Model.OldPath} → {Model.Path}";

    /// <summary>true = tudo no stage; null = parcial; false = nada no stage.</summary>
    public bool? StageState => (Model.HasStagedChanges, Model.HasUnstagedChanges) switch
    {
        (true, false) => true,
        (true, true) => null,
        _ => false,
    };
}

public sealed partial class DiffLineViewModel(DiffLine line) : ObservableObject
{
    public DiffLine Line { get; } = line;

    [ObservableProperty]
    private bool _isSelected;

    public string OldNumber => Line.OldLineNumber?.ToString() ?? string.Empty;
    public string NewNumber => Line.NewLineNumber?.ToString() ?? string.Empty;

    public string Prefix => Line.Type switch
    {
        DiffLineType.Add => "+",
        DiffLineType.Delete => "−",
        _ => " ",
    };

    public string Text => Line.Text.TrimEnd('\r');
    public bool IsAdd => Line.Type == DiffLineType.Add;
    public bool IsDelete => Line.Type == DiffLineType.Delete;
    public bool IsHunk => Line.Type == DiffLineType.HunkHeader;
    public bool IsMeta => Line.Type == DiffLineType.NoNewlineMarker;
    public bool IsChange => Line.IsChange;
}

public sealed class CommitItemViewModel(Commit model)
{
    public Commit Model { get; } = model;
    public string Summary => Model.Summary;
    public string ShortSha => Model.ShortSha;
    public string Body => Model.Body;
    public bool HasBody => Model.Body.Length > 0;
    public string Meta => $"{Model.Author.Name} · {FormatRelative(Model.Author.Date)}";
    public string Refs => string.Join("  ", Model.Refs);
    public bool HasRefs => Model.Refs.Count > 0;
    public string Author => $"{Model.Author.Name} <{Model.Author.Email}>";
    public string Date => Model.Author.Date.LocalDateTime.ToString("g");

    public string Details =>
        $"{Model.Summary}\n\n{(Model.Body.Length > 0 ? Model.Body + "\n\n" : string.Empty)}" +
        $"{Model.Sha}\n{Model.Author.Name} <{Model.Author.Email}> · {Model.Author.Date.LocalDateTime:g}";

    private static string FormatRelative(DateTimeOffset date)
    {
        var span = DateTimeOffset.Now - date;
        return span switch
        {
            { TotalMinutes: < 1 } => "agora",
            { TotalHours: < 1 } => $"há {(int)span.TotalMinutes} min",
            { TotalDays: < 1 } => $"há {(int)span.TotalHours} h",
            { TotalDays: < 30 } => $"há {(int)span.TotalDays} d",
            _ => date.LocalDateTime.ToString("d"),
        };
    }
}

public sealed class CommitFileItemViewModel(CommittedFileChange model)
    : StatusItemViewModel(model.Path, model.Kind, model.Code)
{
    public CommittedFileChange Model { get; } = model;
}
