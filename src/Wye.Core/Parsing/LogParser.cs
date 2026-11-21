using System.Globalization;
using Wye.Core.Models;

namespace Wye.Core.Parsing;

/// <summary>
/// Formato e parser de `git log`. Segue a técnica do GitHub Desktop
/// (app/src/lib/git/git-delimiter-parser.ts): campos unidos por %x00 e registros separados por -z,
/// de modo que nenhum conteúdo (mensagem, nome) consegue quebrar o parse.
/// </summary>
public static class LogParser
{
    private static readonly string[] Fields =
    [
        "%H",  // sha
        "%h",  // sha curto
        "%s",  // assunto
        "%b",  // corpo
        "%an", // autor
        "%ae",
        "%at", // data do autor (unix)
        "%cn", // committer
        "%ce",
        "%ct",
        "%P",  // pais
        "%D",  // refs
    ];

    /// <summary>Argumentos a serem passados para `git log`.</summary>
    public static IReadOnlyList<string> FormatArgs { get; } = ["-z", "--format=" + string.Join("%x00", Fields)];

    public static IReadOnlyList<Commit> Parse(string output)
    {
        if (output.Length == 0)
        {
            return [];
        }

        var tokens = output.Split('\0');
        // --format (tformat) com -z termina cada registro com NUL, então sobra um token vazio no final.
        var usable = tokens.Length % Fields.Length == 1 && tokens[^1].Length == 0
            ? tokens.Length - 1
            : tokens.Length;
        var count = usable / Fields.Length;
        if (usable % Fields.Length != 0)
        {
            throw new FormatException(
                $"Saída de log inesperada: {tokens.Length} campos não é múltiplo de {Fields.Length}.");
        }

        var commits = new List<Commit>(count);
        for (var c = 0; c < count; c++)
        {
            var f = tokens.AsSpan(c * Fields.Length, Fields.Length);
            commits.Add(new Commit(
                Sha: f[0].TrimStart('\n'),
                ShortSha: f[1],
                Summary: f[2],
                Body: f[3].TrimEnd(),
                Author: new GitIdentity(f[4], f[5], FromUnix(f[6])),
                Committer: new GitIdentity(f[7], f[8], FromUnix(f[9])),
                Parents: SplitNonEmpty(f[10], ' '),
                Refs: SplitNonEmpty(f[11], ',')));
        }

        return commits;
    }

    private static DateTimeOffset FromUnix(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.MinValue;

    private static string[] SplitNonEmpty(string value, char separator) =>
        value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
