using System.Globalization;
using Wye.Core.Models;

namespace Wye.Core.Parsing;

/// <summary>
/// Parser de `git status --porcelain=2 --branch -z`.
/// Mesmo formato usado pelo GitHub Desktop (app/src/lib/status-parser.ts), que reconhece
/// as entradas '1' (alterado), '2' (renomeado/copiado), 'u' (conflito), '?' (não rastreado) e '!' (ignorado).
/// </summary>
public static class StatusParser
{
    public static StatusResult Parse(string output)
    {
        var tokens = output.Split('\0');
        var files = new List<FileChange>();
        string? head = null, oid = null, upstream = null;
        int ahead = 0, behind = 0;

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Length == 0)
            {
                continue;
            }

            switch (token[0])
            {
                case '#':
                    ParseHeader(token, ref head, ref oid, ref upstream, ref ahead, ref behind);
                    break;
                case '1':
                    files.Add(ParseOrdinary(token));
                    break;
                case '2':
                    // Na forma -z o caminho original vem no próximo token.
                    var origPath = i + 1 < tokens.Length ? tokens[++i] : null;
                    files.Add(ParseRenamedOrCopied(token, origPath));
                    break;
                case 'u':
                    files.Add(ParseUnmerged(token));
                    break;
                case '?':
                    files.Add(new FileChange(token[2..], null, '?', '?', ChangeKind.Untracked));
                    break;
                case '!':
                    // Ignorados: não interessam para a UI.
                    break;
                default:
                    throw new FormatException($"Entrada de status desconhecida: '{token}'");
            }
        }

        return new StatusResult(new BranchStatus(head, oid, upstream, ahead, behind), files);
    }

    private static void ParseHeader(
        string line, ref string? head, ref string? oid, ref string? upstream, ref int ahead, ref int behind)
    {
        var parts = line.Split(' ');
        if (parts.Length < 3)
        {
            return;
        }

        switch (parts[1])
        {
            case "branch.oid":
                oid = parts[2] == "(initial)" ? null : parts[2];
                break;
            case "branch.head":
                head = parts[2] == "(detached)" ? null : parts[2];
                break;
            case "branch.upstream":
                upstream = parts[2];
                break;
            case "branch.ab" when parts.Length >= 4:
                ahead = int.Parse(parts[2].TrimStart('+'), CultureInfo.InvariantCulture);
                behind = int.Parse(parts[3].TrimStart('-'), CultureInfo.InvariantCulture);
                break;
        }
    }

    // 1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>
    private static FileChange ParseOrdinary(string line)
    {
        var parts = line.Split(' ', 9);
        if (parts.Length != 9)
        {
            throw new FormatException($"Entrada '1' inválida: '{line}'");
        }

        var xy = parts[1];
        return new FileChange(parts[8], null, xy[0], xy[1], KindFromXY(xy[0], xy[1]));
    }

    // 2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <X><score> <path>
    private static FileChange ParseRenamedOrCopied(string line, string? origPath)
    {
        var parts = line.Split(' ', 10);
        if (parts.Length != 10)
        {
            throw new FormatException($"Entrada '2' inválida: '{line}'");
        }

        var xy = parts[1];
        var kind = parts[8][0] == 'C' ? ChangeKind.Copied : ChangeKind.Renamed;
        return new FileChange(parts[9], origPath, xy[0], xy[1], kind);
    }

    // u <XY> <sub> <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>
    private static FileChange ParseUnmerged(string line)
    {
        var parts = line.Split(' ', 11);
        if (parts.Length != 11)
        {
            throw new FormatException($"Entrada 'u' inválida: '{line}'");
        }

        var xy = parts[1];
        return new FileChange(parts[10], null, xy[0], xy[1], ChangeKind.Conflicted);
    }

    private static ChangeKind KindFromXY(char x, char y)
    {
        // O lado do index tem prioridade (ex.: "A." = adicionado, "AM" = adicionado e modificado depois).
        var code = x != '.' ? x : y;
        return code switch
        {
            'A' => ChangeKind.Added,
            'D' => ChangeKind.Deleted,
            'R' => ChangeKind.Renamed,
            'C' => ChangeKind.Copied,
            'T' => ChangeKind.TypeChanged,
            _ => ChangeKind.Modified,
        };
    }

    /// <summary>Converte um código de `--name-status` (ex.: "M", "R100") para <see cref="ChangeKind"/>.</summary>
    public static ChangeKind KindFromNameStatus(char code) => code switch
    {
        'A' => ChangeKind.Added,
        'D' => ChangeKind.Deleted,
        'R' => ChangeKind.Renamed,
        'C' => ChangeKind.Copied,
        'T' => ChangeKind.TypeChanged,
        'U' => ChangeKind.Conflicted,
        _ => ChangeKind.Modified,
    };
}
