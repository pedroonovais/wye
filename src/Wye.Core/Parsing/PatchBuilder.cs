using System.Text;
using Wye.Core.Models;

namespace Wye.Core.Parsing;

public enum PatchDirection
{
    /// <summary>Patch aplicado normalmente (`git apply --cached`): usado para preparar (stage) linhas.</summary>
    Forward,

    /// <summary>Patch aplicado ao contrário (`git apply --cached --reverse`): usado para retirar linhas do stage.</summary>
    Reverse,
}

/// <summary>
/// Gera um patch contendo só as linhas selecionadas de um <see cref="FileDiff"/>, para stage parcial.
/// Mesma ideia do app/src/lib/patch-formatter.ts do GitHub Desktop, que depois chama
/// `git apply --cached --unidiff-zero` (app/src/lib/git/apply.ts:54).
///
/// Regras (Forward): linha '+' não selecionada é descartada; linha '-' não selecionada vira contexto.
/// Regras (Reverse): o lado "novo" precisa bater com o index, então é o inverso:
/// '+' não selecionada vira contexto; '-' não selecionada é descartada.
/// </summary>
public static class PatchBuilder
{
    /// <returns>O patch, ou null se nenhuma linha alterada foi selecionada.</returns>
    public static string? Build(FileDiff diff, IReadOnlySet<int> selectedLineIndexes, PatchDirection direction)
    {
        if (diff.IsBinary || diff.Hunks.Count == 0)
        {
            return null;
        }

        var allChanges = diff.Lines.Where(l => l.IsChange).Select(l => l.Index).ToList();
        var selectedChanges = allChanges.Where(selectedLineIndexes.Contains).ToList();
        if (selectedChanges.Count == 0)
        {
            return null;
        }

        var isPartial = selectedChanges.Count != allChanges.Count;
        var body = new StringBuilder();
        var delta = 0; // diferença acumulada (novo - antigo) dos hunks já emitidos

        foreach (var hunk in diff.Hunks)
        {
            var output = new List<string>();
            int oldCount = 0, newCount = 0;
            var hasSelected = false;
            var lastLineWasDropped = false;

            for (var idx = hunk.FirstLineIndex + 1; idx <= hunk.LastLineIndex; idx++)
            {
                var line = diff.Lines[idx];
                var selected = selectedLineIndexes.Contains(line.Index);

                switch (line.Type)
                {
                    case DiffLineType.Context:
                        output.Add(" " + line.Text);
                        oldCount++;
                        newCount++;
                        lastLineWasDropped = false;
                        break;

                    case DiffLineType.Add when selected:
                        output.Add("+" + line.Text);
                        newCount++;
                        hasSelected = true;
                        lastLineWasDropped = false;
                        break;

                    case DiffLineType.Delete when selected:
                        output.Add("-" + line.Text);
                        oldCount++;
                        hasSelected = true;
                        lastLineWasDropped = false;
                        break;

                    case DiffLineType.Add when direction == PatchDirection.Reverse:
                    case DiffLineType.Delete when direction == PatchDirection.Forward:
                        // Mudança não selecionada que existe na base: vira contexto.
                        output.Add(" " + line.Text);
                        oldCount++;
                        newCount++;
                        lastLineWasDropped = false;
                        break;

                    case DiffLineType.Add:
                    case DiffLineType.Delete:
                        lastLineWasDropped = true;
                        break;

                    case DiffLineType.NoNewlineMarker:
                        // O marcador pertence à linha anterior; some junto com ela.
                        if (!lastLineWasDropped)
                        {
                            output.Add(line.Text);
                        }

                        break;
                }
            }

            if (!hasSelected)
            {
                continue;
            }

            int oldStart, newStart;
            if (direction == PatchDirection.Forward)
            {
                oldStart = hunk.OldStart;
                newStart = AdjustStart(hunk.OldStart, oldCount, newCount, delta);
            }
            else
            {
                newStart = hunk.NewStart;
                oldStart = AdjustStart(hunk.NewStart, newCount, oldCount, -delta);
            }

            delta += newCount - oldCount;
            body.Append($"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@\n");
            foreach (var l in output)
            {
                body.Append(l).Append('\n');
            }
        }

        if (body.Length == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var h in BuildHeader(diff, isPartial, direction))
        {
            sb.Append(h).Append('\n');
        }

        sb.Append(body);
        return sb.ToString();
    }

    // Para hunks de tamanho 0 o git usa como "start" a linha anterior; mantemos a convenção.
    private static int AdjustStart(int baseStart, int baseCount, int otherCount, int delta)
    {
        var start = baseStart + delta;
        if (baseCount == 0 && otherCount > 0)
        {
            start++;
        }
        else if (baseCount > 0 && otherCount == 0)
        {
            start--;
        }

        return Math.Max(start, 0);
    }

    private static IEnumerable<string> BuildHeader(FileDiff diff, bool isPartial, PatchDirection direction)
    {
        var isNewFile = diff.OldPath is null;
        var isDeletedFile = diff.NewPath is null;

        // Arquivo novo inteiro, arquivo apagado inteiro ou mudança normal: o cabeçalho original serve.
        if (!isPartial || (!isNewFile && !isDeletedFile))
        {
            return diff.HeaderLines.Where(h => !h.StartsWith("index ", StringComparison.Ordinal));
        }

        // Seleção parcial de arquivo novo/apagado: o resultado não é mais "criar" nem "apagar" o arquivo
        // em um dos lados, então reescrevemos como modificação simples.
        var path = diff.NewPath ?? diff.OldPath!;
        if (isNewFile && direction == PatchDirection.Forward)
        {
            // Criar o arquivo com só parte das linhas continua sendo uma criação válida.
            return diff.HeaderLines.Where(h => !h.StartsWith("index ", StringComparison.Ordinal));
        }

        return [$"diff --git a/{path} b/{path}", $"--- a/{path}", $"+++ b/{path}"];
    }
}
