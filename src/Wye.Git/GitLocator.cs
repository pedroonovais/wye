using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Wye.Git;

/// <summary>
/// Encontra o git do sistema. Diferente do GitHub Desktop, que embute o próprio git (dugite;
/// no pacote Linux: resources/app/git/bin/git, versão 2.45.1), usamos o git já instalado e
/// exigimos uma versão mínima.
/// </summary>
public static partial class GitLocator
{
    /// <summary>porcelain=2 com --branch e `%(trailers)` exigem git moderno; 2.30 é de dez/2020.</summary>
    public static Version MinimumVersion { get; } = new(2, 30);

    [GeneratedRegex(@"git version (\d+)\.(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionRegex();

    public static string? FindGit()
    {
        var overridePath = Environment.GetEnvironmentVariable("WYE_GIT");
        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var exe = OperatingSystem.IsWindows() ? "git.exe" : "git";
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim('"'), exe))
            .ToList();

        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                     })
            {
                if (!string.IsNullOrEmpty(root))
                {
                    candidates.Add(Path.Combine(root, "Git", "cmd", "git.exe"));
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.AddRange(["/opt/homebrew/bin/git", "/usr/local/bin/git", "/usr/bin/git"]);
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public static async Task<Version?> GetVersionAsync(string gitPath, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(gitPath, "--version")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p is null)
        {
            return null;
        }

        var output = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return ParseVersion(output);
    }

    public static Version? ParseVersion(string output)
    {
        var m = VersionRegex().Match(output);
        if (!m.Success)
        {
            return null;
        }

        return new Version(
            int.Parse(m.Groups[1].Value),
            int.Parse(m.Groups[2].Value),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    /// <summary>Localiza o git e valida a versão. Lança <see cref="InvalidOperationException"/> com mensagem amigável.</summary>
    public static async Task<GitRunner> CreateRunnerAsync(CancellationToken ct = default)
    {
        var path = FindGit() ?? throw new InvalidOperationException(
            "Git não encontrado. Instale o git (Linux: pelo gerenciador de pacotes; " +
            "Windows: https://git-scm.com/download/win; macOS: `xcode-select --install` ou Homebrew).");

        var version = await GetVersionAsync(path, ct);
        if (version is null || version < MinimumVersion)
        {
            throw new InvalidOperationException(
                $"Git {version?.ToString() ?? "desconhecido"} em {path}; é necessário {MinimumVersion} ou mais novo.");
        }

        return new GitRunner(path);
    }
}
