using System.Diagnostics;
using System.Text;

namespace Wye.Git;

public sealed record GitResult(int ExitCode, string Stdout, string Stderr);

public sealed class GitException(string message, string[] args, GitResult result)
    : Exception(message)
{
    public IReadOnlyList<string> Args { get; } = args;
    public GitResult Result { get; } = result;
}

public sealed class GitRunOptions
{
    public string? Stdin { get; init; }

    /// <summary>Códigos de saída considerados sucesso (padrão: só 0).</summary>
    public IReadOnlySet<int> SuccessExitCodes { get; init; } = new HashSet<int> { 0 };

    /// <summary>Recebe as linhas de stderr à medida que chegam (progresso de fetch/push/clone com --progress).</summary>
    public IProgress<string>? Progress { get; init; }

    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Limite de stdout em bytes; se passar, o processo é morto e <see cref="GitOutputTooLargeException"/> é lançada.</summary>
    public long? MaxOutputBytes { get; init; }
}

public sealed class GitOutputTooLargeException(long limit)
    : Exception($"Saída do git passou de {limit} bytes.");

/// <summary>
/// Executa o git como processo filho — mesma estratégia do GitHub Desktop
/// (app/src/lib/git/core.ts → dugite.exec), mas usando o git instalado no sistema.
/// </summary>
public sealed class GitRunner(string gitPath)
{
    /// <summary>Quanto de stderr guardar para mensagens de erro (o Desktop guarda 256 KB, core.ts:225).</summary>
    private const int StderrTailChars = 256 * 1024;

    public string GitPath { get; } = gitPath;

    public async Task<GitResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        GitRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new GitRunOptions();
        var psi = new ProcessStartInfo(GitPath)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // ArgumentList faz o escape correto em todas as plataformas (sem montar string de shell).
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        ConfigureEnvironment(psi.Environment, options.Environment);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = ReadStdoutAsync(process, options.MaxOutputBytes, cancellationToken);
        var stderrTask = ReadStderrAsync(process.StandardError, options.Progress, cancellationToken);

        await using (var registration = cancellationToken.Register(() => TryKill(process)))
        {
            if (options.Stdin is not null)
            {
                // Sem BOM: o git leria o BOM como parte da mensagem/patch.
                var bytes = new UTF8Encoding(false).GetBytes(options.Stdin);
                await process.StandardInput.BaseStream.WriteAsync(bytes, cancellationToken);
            }

            process.StandardInput.Close();

            string stdout;
            try
            {
                stdout = await stdoutTask;
            }
            catch (GitOutputTooLargeException)
            {
                TryKill(process);
                throw;
            }

            var stderr = await stderrTask;
            await process.WaitForExitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var result = new GitResult(process.ExitCode, stdout, stderr);
            if (!options.SuccessExitCodes.Contains(result.ExitCode))
            {
                var message = stderr.Trim();
                if (message.Length == 0)
                {
                    message = $"git {string.Join(' ', args)} terminou com código {result.ExitCode}";
                }

                throw new GitException(message, [.. args], result);
            }

            return result;
        }
    }

    private static void ConfigureEnvironment(IDictionary<string, string?> env, IReadOnlyDictionary<string, string>? extra)
    {
        // Mesmo motivo do Desktop (core.ts:246-250): o git não deve achar que fala com um terminal.
        env["TERM"] = "dumb";

        // Melhoria: nunca travar esperando senha num terminal invisível. Sem helper de credencial,
        // a operação falha na hora com mensagem clara em vez de ficar pendurada.
        env["GIT_TERMINAL_PROMPT"] = "0";

        // Mensagens de erro em inglês estável, para podermos reconhecê-las.
        env["LANGUAGE"] = "C";

        // SSH sem TTY: falha em vez de pedir passphrase/host key num terminal que não existe.
        // Chaves carregadas no ssh-agent continuam funcionando. Respeita a configuração do usuário.
        if (!env.ContainsKey("GIT_SSH_COMMAND") || string.IsNullOrEmpty(env["GIT_SSH_COMMAND"]))
        {
            env["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
        }

        if (extra is not null)
        {
            foreach (var (k, v) in extra)
            {
                env[k] = v;
            }
        }
    }

    private static async Task<string> ReadStdoutAsync(Process process, long? maxBytes, CancellationToken ct)
    {
        if (maxBytes is null)
        {
            return await process.StandardOutput.ReadToEndAsync(ct);
        }

        var sb = new StringBuilder();
        var buffer = new char[8192];
        long total = 0;
        int read;
        while ((read = await process.StandardOutput.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new GitOutputTooLargeException(maxBytes.Value);
            }

            sb.Append(buffer, 0, read);
        }

        return sb.ToString();
    }

    private static async Task<string> ReadStderrAsync(StreamReader reader, IProgress<string>? progress, CancellationToken ct)
    {
        var all = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, ct)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                all.Append(c);

                // O git reescreve a linha de progresso com '\r'; tratamos '\r' e '\n' como fim de linha.
                if (c is '\r' or '\n')
                {
                    if (line.Length > 0)
                    {
                        progress?.Report(line.ToString());
                        line.Clear();
                    }
                }
                else
                {
                    line.Append(c);
                }
            }

            if (all.Length > StderrTailChars * 2)
            {
                all.Remove(0, all.Length - StderrTailChars);
            }
        }

        if (line.Length > 0)
        {
            progress?.Report(line.ToString());
        }

        return all.Length > StderrTailChars ? all.ToString(all.Length - StderrTailChars, StderrTailChars) : all.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // já terminou
        }
    }
}
