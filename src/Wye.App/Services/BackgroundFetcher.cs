namespace Wye.App.Services;

/// <summary>
/// Fetch periódico. Mesmos números do GitHub Desktop
/// (app/src/lib/stores/helpers/background-fetcher.ts:11,17,147): intervalo padrão de 1 hora
/// mais um desvio aleatório, para que várias instâncias não batam no servidor ao mesmo tempo.
/// </summary>
public sealed class BackgroundFetcher : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    private readonly Func<CancellationToken, Task> _fetch;
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _interval;

    public BackgroundFetcher(Func<CancellationToken, Task> fetch, TimeSpan? interval = null)
    {
        _fetch = fetch;
        _interval = interval ?? DefaultInterval;
        _ = LoopAsync(_cts.Token);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var skew = TimeSpan.FromSeconds(Random.Shared.Next((int)MaxSkew.TotalSeconds));
        using var timer = new PeriodicTimer(_interval + skew);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await _fetch(ct);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // Falha em segundo plano (sem rede, sem credencial) não deve incomodar o usuário.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
