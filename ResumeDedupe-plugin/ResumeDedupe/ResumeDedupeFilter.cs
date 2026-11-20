using System.Collections.Concurrent;
using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ResumeDedupe.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ResumeDedupe;

/// <summary>
/// Continue Watching (/UserItems/Resume, /Users/{id}/Items/Resume):
///   um (ou N) episodio(s) por serie, o mais recente primeiro.
/// Next Up (/Shows/NextUp):
///   esconde series que ja aparecem em Continue Watching (clientes como o Moonfin
///   juntam as duas listas numa linha so).
///
/// Detalhes importantes:
///  - Trabalha no nivel MVC (objeto, nao bytes): sem problemas com gzip/brotli/proxy.
///  - Respeita paginacao (startIndex/limit): busca do inicio com folga, deduplica
///    globalmente e so entao devolve a janela pedida. Assim as paginas do cliente
///    nao repetem nem pulam itens.
///  - Qualquer erro devolve a resposta original do Jellyfin.
/// </summary>
public class ResumeDedupeFilter : IAsyncActionFilter
{
    private const int Factor = 5;          // quantas vezes mais itens pedir
    private const int Pad = 30;            // folga minima
    private const int MaxFetch = 800;      // teto de itens pedidos ao Jellyfin

    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan NoWaitFor = TimeSpan.FromMinutes(5);

    private static readonly ConcurrentDictionary<string, UserState> States = new();

    private static readonly PropertyInfo? ItemsProp = typeof(QueryResult<BaseItemDto>).GetProperty("Items");
    private static readonly PropertyInfo? TotalProp = typeof(QueryResult<BaseItemDto>).GetProperty("TotalRecordCount");
    private static readonly PropertyInfo? StartProp = typeof(QueryResult<BaseItemDto>).GetProperty("StartIndex");

    private static int _loggedResume;
    private static int _loggedNextUp;
    private static int _loggedUnknownType;
    private static int _loggedSetFailure;

    private readonly ILogger<ResumeDedupeFilter> _logger;

    public ResumeDedupeFilter(ILogger<ResumeDedupeFilter> logger)
    {
        _logger = logger;
    }

    private enum Kind
    {
        None,
        Resume,
        NextUp
    }

    /// <summary>Janela pedida pelo cliente e se a consulta foi ampliada.</summary>
    private readonly record struct Window(int Start, int? Limit, bool Applied, int Fetched);

    private sealed class UserState
    {
        public readonly object Gate = new();
        public DateTime At = DateTime.MinValue;
        public DateTime NoWaitUntil = DateTime.MinValue;
        public HashSet<Guid> Series = new();
        public TaskCompletionSource<bool> Next = NewSignal();
    }

    private static TaskCompletionSource<bool> NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var kind = Kind.None;
        var window = default(Window);
        var cfg = GetConfig();
        var startedAt = DateTime.UtcNow;

        try
        {
            kind = Classify(context.HttpContext.Request);
            if (kind != Kind.None)
            {
                var enabled = kind == Kind.Resume
                    ? cfg.DedupeContinueWatching || cfg.HideNextUpInContinueWatching
                    : cfg.HideNextUpInContinueWatching;

                if (!enabled)
                {
                    kind = Kind.None;
                }
                else if (kind == Kind.Resume && !cfg.DedupeContinueWatching)
                {
                    window = default; // so registrar series para o Next Up: nao ampliar
                }
                else
                {
                    window = Prepare(context);
                }
            }
        }
        catch (Exception ex)
        {
            kind = Kind.None;
            _logger.LogWarning(ex, "ResumeDedupe: falha ao preparar a consulta; seguindo sem alterar");
        }

        var executed = await next().ConfigureAwait(false);

        if (kind == Kind.None || executed.Exception is not null)
        {
            return;
        }

        try
        {
            if (executed.Result is ObjectResult { Value: QueryResult<BaseItemDto> result })
            {
                if (kind == Kind.Resume)
                {
                    ProcessResume(result, cfg, window, GetUserKey(context));
                }
                else
                {
                    await ProcessNextUpAsync(result, cfg, window, GetUserKey(context), startedAt).ConfigureAwait(false);
                }
            }
            else if (Interlocked.Exchange(ref _loggedUnknownType, 1) == 0)
            {
                _logger.LogWarning(
                    "ResumeDedupe: tipo de resposta inesperado ({Type}) em {Path}; nada foi alterado",
                    (executed.Result as ObjectResult)?.Value?.GetType().FullName ?? executed.Result?.GetType().FullName,
                    context.HttpContext.Request.Path.Value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ResumeDedupe: falha ao processar; devolvendo resposta original");
        }
    }

    // ----------------------------------------------------------- PREPARACAO

    /// <summary>
    /// Se o cliente pagina (startIndex/limit), busca do inicio ate o fim da janela com folga,
    /// para que a deduplicacao seja global e a janela fique cheia.
    /// </summary>
    private static Window Prepare(ActionExecutingContext context)
    {
        var args = context.ActionArguments;
        var start = Math.Max(0, ReadInt(args, "startIndex") ?? 0);
        var limit = ReadInt(args, "limit");

        if (limit is <= 0)
        {
            return new Window(start, limit, false, 0);
        }

        if (start == 0 && limit is null)
        {
            return new Window(0, null, false, 0); // lista completa: nada a ampliar
        }

        if (limit is null)
        {
            // Sem limite mas com offset: buscar tudo do inicio e cortar depois.
            if (args.ContainsKey("startIndex"))
            {
                args["startIndex"] = 0;
                return new Window(start, null, true, 0);
            }

            return new Window(start, null, false, 0);
        }

        var needed = (long)start + limit.Value;
        var wide = Math.Min(Math.Max(needed * Factor, needed + Pad), MaxFetch);
        if (wide < needed || wide > int.MaxValue || !args.ContainsKey("limit"))
        {
            return new Window(start, limit, false, 0); // pagina muito profunda: sem ampliar
        }

        args["limit"] = (int)wide;
        if (start > 0 && args.ContainsKey("startIndex"))
        {
            args["startIndex"] = 0;
        }

        return new Window(start, limit, true, (int)wide);
    }

    // -------------------------------------------------------- CONTINUE WATCHING

    private void ProcessResume(QueryResult<BaseItemDto> result, PluginConfiguration cfg, Window window, string userKey)
    {
        var raw = result.Items?.ToList() ?? new List<BaseItemDto>();
        var originalTotal = result.TotalRecordCount;

        var max = cfg.DedupeContinueWatching ? Math.Max(1, cfg.MaxEpisodesPerSeries) : int.MaxValue;
        var deduped = Dedupe(raw, max);
        var page = Slice(deduped, window);

        Apply(result, raw.Count, deduped.Count, page, window, originalTotal);

        if (cfg.HideNextUpInContinueWatching && userKey.Length > 0)
        {
            var series = new HashSet<Guid>(SeriesIds(page));
            var state = States.GetOrAdd(userKey, _ => new UserState());
            TaskCompletionSource<bool> previous;
            lock (state.Gate)
            {
                state.Series = series;
                state.At = DateTime.UtcNow;
                state.NoWaitUntil = DateTime.MinValue; // este cliente envia Resume: voltar a esperar por ele
                previous = state.Next;
                state.Next = NewSignal();
            }

            previous.TrySetResult(true);

            if (States.Count > 2000)
            {
                Prune();
            }
        }

        if (raw.Count != page.Count && Interlocked.Exchange(ref _loggedResume, 1) == 0)
        {
            _logger.LogInformation(
                "ResumeDedupe: Continue Watching deduplicado ({Before} -> {After} itens). Funcionando.",
                raw.Count,
                page.Count);
        }
    }

    // ------------------------------------------------------------------ NEXT UP

    private async Task ProcessNextUpAsync(
        QueryResult<BaseItemDto> result,
        PluginConfiguration cfg,
        Window window,
        string userKey,
        DateTime startedAt)
    {
        var raw = result.Items?.ToList() ?? new List<BaseItemDto>();
        var originalTotal = result.TotalRecordCount;
        var items = raw;

        if (cfg.HideNextUpInContinueWatching && userKey.Length > 0)
        {
            var inResume = await GetResumeSeriesAsync(userKey, startedAt).ConfigureAwait(false);
            if (inResume is { Count: > 0 })
            {
                items = raw
                    .Where(i => !(i.SeriesId is { } sid && inResume.Contains(sid)))
                    .ToList();
            }
        }

        var deduped = Dedupe(items, 1);
        var page = Slice(deduped, window);

        Apply(result, raw.Count, deduped.Count, page, window, originalTotal);

        if (raw.Count != page.Count && Interlocked.Exchange(ref _loggedNextUp, 1) == 0)
        {
            _logger.LogInformation(
                "ResumeDedupe: Next Up filtrado ({Before} -> {After} itens). Funcionando.",
                raw.Count,
                page.Count);
        }
    }

    /// <summary>
    /// Series visiveis em Continue Watching para o usuario. Se o Resume do mesmo ciclo de
    /// atualizacao ainda nao chegou (clientes pedem as duas listas em paralelo), espera
    /// ate 1,5 s. Se o cliente nunca envia Resume junto, para de esperar por 5 minutos.
    /// </summary>
    private static async Task<HashSet<Guid>?> GetResumeSeriesAsync(string userKey, DateTime requestStart)
    {
        var state = States.GetOrAdd(userKey, _ => new UserState());
        var cutoff = requestStart - Slack;

        bool inSync;
        bool noWait;
        Task signal;
        lock (state.Gate)
        {
            inSync = state.At >= cutoff;
            noWait = DateTime.UtcNow < state.NoWaitUntil;
            signal = state.Next.Task;
        }

        if (!inSync && !noWait)
        {
            var finished = await Task.WhenAny(signal, Task.Delay(MaxWait)).ConfigureAwait(false) == signal;
            if (!finished)
            {
                lock (state.Gate)
                {
                    state.NoWaitUntil = DateTime.UtcNow + NoWaitFor;
                }
            }
        }

        lock (state.Gate)
        {
            if (state.At >= cutoff || DateTime.UtcNow - state.At <= MaxAge)
            {
                return state.Series;
            }

            return null;
        }
    }

    private static void Prune()
    {
        var limit = DateTime.UtcNow.AddHours(-1);
        foreach (var pair in States)
        {
            if (pair.Value.At < limit)
            {
                States.TryRemove(pair.Key, out _);
            }
        }
    }

    // ------------------------------------------------------------------ HELPERS

    private static List<BaseItemDto> Dedupe(IEnumerable<BaseItemDto> items, int maxPerSeries)
    {
        var ids = new HashSet<Guid>();
        var counts = new Dictionary<Guid, int>();
        var kept = new List<BaseItemDto>();

        foreach (var item in items)
        {
            if (item is null)
            {
                continue;
            }

            // Mesmo item repetido na lista.
            if (item.Id != Guid.Empty && !ids.Add(item.Id))
            {
                continue;
            }

            if (item.Type == BaseItemKind.Episode && item.SeriesId is { } seriesId && seriesId != Guid.Empty)
            {
                counts.TryGetValue(seriesId, out var count);
                if (count >= maxPerSeries)
                {
                    continue;
                }

                counts[seriesId] = count + 1;
            }

            kept.Add(item);
        }

        return kept;
    }

    private static List<BaseItemDto> Slice(List<BaseItemDto> deduped, Window window)
    {
        if (!window.Applied)
        {
            // Sem ampliacao: a lista recebida ja e a janela do cliente.
            return deduped;
        }

        var query = deduped.Skip(window.Start);
        return (window.Limit.HasValue ? query.Take(window.Limit.Value) : query).ToList();
    }

    private static IEnumerable<Guid> SeriesIds(IEnumerable<BaseItemDto> items)
    {
        foreach (var item in items)
        {
            if (item.Type == BaseItemKind.Episode && item.SeriesId is { } seriesId && seriesId != Guid.Empty)
            {
                yield return seriesId;
            }
        }
    }

    /// <summary>Grava a nova lista, o total e o StartIndex (via reflexao: imune a mudancas de setter).</summary>
    private void Apply(
        QueryResult<BaseItemDto> result,
        int rawCount,
        int dedupedCount,
        List<BaseItemDto> page,
        Window window,
        int originalTotal)
    {
        if (!window.Applied && page.Count == rawCount)
        {
            return; // nada mudou
        }

        if (!TrySet(ItemsProp, result, ItemsProp is { PropertyType.IsArray: true } ? (object)page.ToArray() : page))
        {
            return;
        }

        if (originalTotal > 0)
        {
            int total;
            if (window.Applied)
            {
                // Buscamos do inicio: se o Jellyfin devolveu menos que o pedido, temos tudo.
                var fetchedAll = window.Fetched == 0 || rawCount < window.Fetched;
                total = fetchedAll
                    ? dedupedCount
                    : dedupedCount + Math.Max(0, originalTotal - rawCount);
            }
            else if (window.Start == 0 && window.Limit is null)
            {
                total = dedupedCount; // lista completa
            }
            else
            {
                // Pagina isolada (sem ampliar): estimativa pelo que foi removido.
                total = Math.Max(dedupedCount, originalTotal - (rawCount - dedupedCount));
            }

            TrySet(TotalProp, result, total);
        }

        if (window.Applied)
        {
            TrySet(StartProp, result, window.Start);
        }
    }

    private bool TrySet(PropertyInfo? property, object target, object? value)
    {
        try
        {
            if (property is null || !property.CanWrite)
            {
                throw new InvalidOperationException($"Propriedade {property?.Name ?? "?"} nao gravavel");
            }

            property.SetValue(target, value);
            return true;
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _loggedSetFailure, 1) == 0)
            {
                _logger.LogWarning(ex, "ResumeDedupe: nao foi possivel alterar a resposta; nada foi alterado");
            }

            return false;
        }
    }

    private static int? ReadInt(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            _ => null
        };
    }

    private static Kind Classify(HttpRequest request)
    {
        var path = request.Path.Value;
        if (string.IsNullOrEmpty(path))
        {
            return Kind.None;
        }

        if (path.EndsWith("/Resume", StringComparison.OrdinalIgnoreCase)
            && (path.EndsWith("/Items/Resume", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/UserItems/Resume", StringComparison.OrdinalIgnoreCase)))
        {
            return Kind.Resume;
        }

        // Next Up de uma serie especifica (pagina da serie) nao entra.
        if (path.EndsWith("/NextUp", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/Shows/NextUp", StringComparison.OrdinalIgnoreCase)
            && !request.Query.ContainsKey("seriesId"))
        {
            return Kind.NextUp;
        }

        return Kind.None;
    }

    private static string GetUserKey(ActionExecutingContext context)
    {
        var http = context.HttpContext;
        string? raw = http.User.FindFirst("Jellyfin-UserId")?.Value;

        if (raw is null && context.ActionArguments.TryGetValue("userId", out var arg))
        {
            raw = arg?.ToString();
        }

        raw ??= http.Request.Query["userId"].FirstOrDefault()
            ?? http.Request.RouteValues["userId"]?.ToString();

        return Guid.TryParse(raw, out var id) && id != Guid.Empty ? id.ToString("N") : string.Empty;
    }

    private static PluginConfiguration GetConfig()
        => Plugin.Instance?.Configuration ?? new PluginConfiguration();
}
