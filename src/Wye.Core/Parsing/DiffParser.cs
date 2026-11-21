using System.Globalization;
using System.Text.RegularExpressions;
using Wye.Core.Models;

namespace Wye.Core.Parsing;

/// <summary>
/// Parser de diff unificado de UM arquivo (saída de `git diff --no-ext-diff -- &lt;path&gt;`).
/// Equivalente ao app/src/lib/diff-parser.ts do GitHub Desktop.
/// </summary>
public static partial class DiffParser
{
    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(.*)$")]
    private static partial Regex HunkHeaderRegex();

    public static FileDiff Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return FileDiff.Empty;
        }

        var rawLines = text.Split('\n');
        var count = rawLines.Length;
        if (count > 0 && rawLines[^1].Length == 0)
        {
            count--; // quebra de linha final
        }

        var header = new List<string>();
        var hunks = new List<DiffHunk>();
        var lines = new List<DiffLine>();
        string? oldPath = null, newPath = null;
        var isBinary = false;

        var i = 0;
        for (; i < count; i++)
        {
            var line = rawLines[i];
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                break;
            }

            if (line.StartsWith("Binary files ", StringComparison.Ordinal) ||
                line.StartsWith("GIT binary patch", StringComparison.Ordinal))
            {
                isBinary = true;
            }
            else if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                oldPath = StripPrefix(line[4..], "a/");
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                newPath = StripPrefix(line[4..], "b/");
            }

            header.Add(line);
        }

        int oldNo = 0, newNo = 0;
        int hunkStartIndex = -1;
        var hunkStartIndexRaw = 0;
        Match? current = null;

        void CloseHunk()
        {
            if (current is null)
            {
                return;
            }

            hunks.Add(new DiffHunk(
                Int(current.Groups[1]), Count(current.Groups[2]),
                Int(current.Groups[3]), Count(current.Groups[4]),
                rawLines[hunkStartIndexRaw],
                hunkStartIndex, lines.Count - 1));
        }

        for (; i < count; i++)
        {
            var line = rawLines[i];

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                break; // só tratamos um arquivo por diff
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var match = HunkHeaderRegex().Match(line);
                if (!match.Success)
                {
                    throw new FormatException($"Cabeçalho de hunk inválido: '{line}'");
                }

                CloseHunk();
                current = match;
                hunkStartIndexRaw = i;
                hunkStartIndex = lines.Count;
                oldNo = Int(match.Groups[1]);
                newNo = Int(match.Groups[3]);
                lines.Add(new DiffLine(lines.Count, DiffLineType.HunkHeader, line, null, null, hunks.Count));
                continue;
            }

            if (current is null)
            {
                continue;
            }

            var hunkIndex = hunks.Count;
            var prefix = line.Length > 0 ? line[0] : ' ';
            var content = line.Length > 0 ? line[1..] : string.Empty;
            switch (prefix)
            {
                case '+':
                    lines.Add(new DiffLine(lines.Count, DiffLineType.Add, content, null, newNo++, hunkIndex));
                    break;
                case '-':
                    lines.Add(new DiffLine(lines.Count, DiffLineType.Delete, content, oldNo++, null, hunkIndex));
                    break;
                case '\\':
                    lines.Add(new DiffLine(lines.Count, DiffLineType.NoNewlineMarker, line, null, null, hunkIndex));
                    break;
                default:
                    lines.Add(new DiffLine(lines.Count, DiffLineType.Context, content, oldNo++, newNo++, hunkIndex));
                    break;
            }
        }

        CloseHunk();
        return new FileDiff(oldPath, newPath, header, hunks, lines, isBinary);
    }

    private static string? StripPrefix(string path, string prefix)
    {
        path = path.TrimEnd('\t', '\r');
        if (path == "/dev/null")
        {
            return null;
        }

        return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
    }

    private static int Int(Group g) => int.Parse(g.Value, CultureInfo.InvariantCulture);

    // Em "@@ -5 +5 @@" o tamanho omitido vale 1.
    private static int Count(Group g) => g.Success ? Int(g) : 1;
}
