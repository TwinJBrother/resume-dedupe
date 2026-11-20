using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ResumeDedupe.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Episode = MediaBrowser.Controller.Entities.TV.Episode;

namespace Jellyfin.Plugin.ResumeDedupe;

/// <summary>
/// Continue Watching (/UserItems/Resume, /Users/{id}/Items/Resume):
///   um (ou N) item(ns) por serie, o mais recente primeiro.
/// Next Up (/Shows/NextUp, so a home):
///   esconde as series que ja tem episodio em andamento (ja aparecem em Continue Watching).
///
/// Como funciona (v1.2):
///  - Filtro MVC: trabalha com objetos, nunca com bytes (gzip/brotli/proxy nao interferem).
///  - O Next Up NAO depende mais do pedido de Continue Watching: o plugin consulta o
///    servidor direto (mesma consulta que o Continue Watching usa, so os IDs das series),
///    com cache de 2 s por usuario. Sem espera, sem corrida entre pedidos.
///  - Paginas exatas: a mesma consulta diz quantos itens serao removidos, entao o plugin
///    pede ao Jellyfin so o necessario (limite + removidos) e a linha vem sempre cheia.
///    Sem duplicatas, nao amplia nada e nao gasta nada extra.
///  - Qualquer erro devolve a resposta original do Jellyfin.
/// </summary>
public class ResumeDedupeFilter : IAsyncActionFilter
{
    private const int Pad = 10;               // folga sobre o numero exato de remocoes
    private const int FallbackFactor = 5;     // se a consulta direta falhar: heuristica antiga
    private const int FallbackPad = 30;
    private const int MaxFetch = 1000;        // teto de itens pedidos ao Jellyfin
    private const int MaxScan = 2000;         // teto de episodios em andamento lidos por usuario
    private const long CacheTtlMs = 2000;
    private const long CacheMaxAgeMs = 60000;
    private const int CachePruneAbove = 200;

    private static readonly ConcurrentDictionary<Guid, CacheEntry> Cache = new();

    private static int _loggedResume;
    private static int _loggedNextUp;
    private static int _loggedUnknownType;
    private static int _loggedLoadFailure;

    private readonly ILogger<ResumeDedupeFilter> _logger;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;

    public ResumeDedupeFilter(
        ILogger<ResumeDedupeFilter> logger,
        ILibraryManager libraryManager,
        IUserManager userManager)
    {
        _logger = logger;
        _library = libraryManager;
        _users = userManager;
    }

    private enum Kind
    {
        None,
        Resume,
        NextUp
    }

    /// <summary>Janela pedida pelo cliente, se a consulta foi ampliada e series a esconder.</summary>
    private readonly record struct Plan(int Start, int? Limit, bool Applied, int Fetched, HashSet<Guid>? Hidden);

    private sealed class CacheEntry
    {
        public CacheEntry(long at, Func<Dictionary<Guid, int>?> load)
        {
            At = at;
            Counts = new Lazy<Dictionary<Guid, int>?>(load, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public long At { get; }

        /// <summary>Episodios em andamento por serie (null = nao foi possivel consultar).</summary>
        public Lazy<Dictionary<Guid, int>?> Counts { get; }
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var kind = Kind.None;
        var plan = default(Plan);
        var cfg = GetConfig();

        try
        {
            kind = Classify(context.HttpContext.Request);
            if (kind != Kind.None)
            {
                var enabled = kind == Kind.Resume
                    ? cfg.DedupeContinueWatching
                    : cfg.HideNextUpInContinueWatching;

                if (!enabled)
                {
                    kind = Kind.None;
                }
                else
                {
                    plan = Prepare(context, kind, cfg, GetUserId(context));
                }
            }
        }
        catch (Exception ex)
        {
            kind = Kind.None;
            plan = default;
            _logger.LogWarning(ex, "ResumeDedupe: falha ao preparar a consulta; seguindo sem alterar");
        }

        var executed = await next().ConfigureAwait(false);

        if (kind == Kind.None || executed.Exception is not null)
        {
            return;
        }

        try
        {
            if (executed.Result is ObjectResult { Value: QueryResult<BaseItemDto> result } holder)
            {
                if (kind == Kind.Resume)
                {
                    ProcessResume(holder, result, cfg, plan);
                }
                else
                {
                    ProcessNextUp(holder, result, plan);
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
            TryTrim(executed, plan);
        }
    }

    // ----------------------------------------------------------- PREPARACAO

    /// <summary>
    /// Decide quantos itens pedir ao Jellyfin para que, depois de remover duplicatas,
    /// a janela do cliente (startIndex/limit) continue cheia. So altera a consulta
    /// quando ha algo a remover.
    /// </summary>
    private Plan Prepare(ActionExecutingContext context, Kind kind, PluginConfiguration cfg, Guid userId)
    {
        var args = context.ActionArguments;
        var start = Math.Max(0, ReadInt(args, "startIndex") ?? 0);
        var limit = ReadInt(args, "limit");

        var counts = userId == Guid.Empty ? null : GetResumeCounts(userId);

        HashSet<Guid>? hidden = null;
        var removable = 0; // quantos itens, no maximo, serao removidos; -1 = desconhecido

        if (kind == Kind.NextUp)
        {
            if (counts is { Count: > 0 })
            {
                hidden = new HashSet<Guid>(counts.Keys);
                removable = hidden.Count; // o Next Up traz no maximo 1 episodio por serie
            }
        }
        else if (counts is null)
        {
            removable = -1;
        }
        else
        {
            var max = Math.Max(1, cfg.MaxEpisodesPerSeries);
            foreach (var count in counts.Values)
            {
                if (count > max)
                {
                    removable += count - max;
                }
            }
        }

        if (limit is <= 0 || removable == 0 || (start == 0 && limit is null))
        {
            return new Plan(start, limit, false, 0, hidden);
        }

        if (limit is null)
        {
            // Sem limite mas com offset: buscar tudo do inicio e cortar depois.
            if (args.ContainsKey("startIndex"))
            {
                args["startIndex"] = 0;
                return new Plan(start, null, true, 0, hidden);
            }

            return new Plan(start, null, false, 0, hidden);
        }

        var needed = (long)start + limit.Value;
        var wide = removable > 0
            ? needed + removable + Pad
            : Math.Max(needed * FallbackFactor, needed + FallbackPad);
        wide = Math.Min(wide, MaxFetch);

        if (wide < needed || !args.ContainsKey("limit"))
        {
            return new Plan(start, limit, false, 0, hidden); // pagina muito profunda: sem ampliar
        }

        args["limit"] = (int)wide;
        if (start > 0 && args.ContainsKey("startIndex"))
        {
            args["startIndex"] = 0;
        }

        return new Plan(start, limit, true, (int)wide, hidden);
    }

    // ------------------------------------------------- CONSULTA DIRETA (CACHE)

    private Dictionary<Guid, int>? GetResumeCounts(Guid userId)
    {
        var now = Environment.TickCount64;
        var entry = Cache.AddOrUpdate(
            userId,
            _ => new CacheEntry(now, () => LoadResumeCounts(userId)),
            (_, old) => now - old.At > CacheTtlMs ? new CacheEntry(now, () => LoadResumeCounts(userId)) : old);

        if (Cache.Count > CachePruneAbove)
        {
            Prune(now);
        }

        return entry.Counts.Value;
    }

    /// <summary>
    /// Mesma consulta que o Jellyfin usa para Continue Watching, mas leve (sem DTOs):
    /// conta os episodios em andamento por serie.
    /// </summary>
    private Dictionary<Guid, int>? LoadResumeCounts(Guid userId)
    {
        try
        {
            var user = _users.GetUserById(userId);
            if (user is null)
            {
                return null;
            }

            var query = new InternalItemsQuery(user)
            {
                IsResumable = true,
                Recursive = true,
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                IsVirtualItem = false,
                CollapseBoxSetItems = false,
                IncludeOwnedItems = true,
                EnableTotalRecordCount = false,
                DtoOptions = new DtoOptions(false),
                Limit = MaxScan
            };

            var counts = new Dictionary<Guid, int>();
            foreach (var item in _library.GetItemList(query))
            {
                if (item is Episode episode && episode.SeriesId != Guid.Empty)
                {
                    counts.TryGetValue(episode.SeriesId, out var current);
                    counts[episode.SeriesId] = current + 1;
                }
            }

            return counts;
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _loggedLoadFailure, 1) == 0)
            {
                _logger.LogWarning(ex, "ResumeDedupe: nao foi possivel consultar os episodios em andamento; usando modo reduzido");
            }

            return null;
        }
    }

    private static void Prune(long now)
    {
        foreach (var pair in Cache)
        {
            if (now - pair.Value.At > CacheMaxAgeMs)
            {
                Cache.TryRemove(pair.Key, out _);
            }
        }
    }

    // -------------------------------------------------------- CONTINUE WATCHING

    private void ProcessResume(ObjectResult holder, QueryResult<BaseItemDto> result, PluginConfiguration cfg, Plan plan)
    {
        var raw = result.Items?.ToList() ?? new List<BaseItemDto>();
        var deduped = Dedupe(raw, Math.Max(1, cfg.MaxEpisodesPerSeries));
        var page = Slice(deduped, plan);

        Publish(holder, result, raw.Count, deduped.Count, page, plan);

        if (deduped.Count != raw.Count && Interlocked.Exchange(ref _loggedResume, 1) == 0)
        {
            _logger.LogInformation(
                "ResumeDedupe: Continue Watching deduplicado ({Before} -> {After} itens). Funcionando.",
                raw.Count,
                deduped.Count);
        }
    }

    // ------------------------------------------------------------------ NEXT UP

    private void ProcessNextUp(ObjectResult holder, QueryResult<BaseItemDto> result, Plan plan)
    {
        var raw = result.Items?.ToList() ?? new List<BaseItemDto>();
        var items = raw;

        if (plan.Hidden is { Count: > 0 } hidden)
        {
            items = raw.Where(i => !InSet(i, hidden)).ToList();
        }

        var deduped = Dedupe(items, 1);
        var page = Slice(deduped, plan);

        Publish(holder, result, raw.Count, deduped.Count, page, plan);

        if (deduped.Count != raw.Count && Interlocked.Exchange(ref _loggedNextUp, 1) == 0)
        {
            _logger.LogInformation(
                "ResumeDedupe: Next Up filtrado ({Before} -> {After} itens). Funcionando.",
                raw.Count,
                deduped.Count);
        }
    }

    // ------------------------------------------------------------------ HELPERS

    private static bool InSet(BaseItemDto item, HashSet<Guid> set)
    {
        if (item.SeriesId is { } seriesId)
        {
            return set.Contains(seriesId);
        }

        return false;
    }

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

            // Episodio conta para a sua serie; um item "Series" (o Jellyfin 12.x pode devolver
            // a serie inteira em Continue Watching) conta para ela mesma.
            var key = Guid.Empty;
            if (item.Type == BaseItemKind.Episode && item.SeriesId is { } seriesId && seriesId != Guid.Empty)
            {
                key = seriesId;
            }
            else if (item.Type == BaseItemKind.Series && item.Id != Guid.Empty)
            {
                key = item.Id;
            }

            if (key != Guid.Empty)
            {
                counts.TryGetValue(key, out var count);
                if (count >= maxPerSeries)
                {
                    continue;
                }

                counts[key] = count + 1;
            }

            kept.Add(item);
        }

        return kept;
    }

    private static List<BaseItemDto> Slice(List<BaseItemDto> deduped, Plan plan)
    {
        if (!plan.Applied)
        {
            // Sem ampliacao: a lista recebida ja e a janela do cliente.
            return deduped;
        }

        IEnumerable<BaseItemDto> query = deduped.Skip(plan.Start);
        if (plan.Limit.HasValue)
        {
            query = query.Take(plan.Limit.Value);
        }

        return query.ToList();
    }

    /// <summary>Troca o resultado por um novo QueryResult com a janela filtrada e o total ajustado.</summary>
    private static void Publish(
        ObjectResult holder,
        QueryResult<BaseItemDto> original,
        int rawCount,
        int dedupedCount,
        List<BaseItemDto> page,
        Plan plan)
    {
        if (!plan.Applied && page.Count == rawCount)
        {
            return; // nada mudou
        }

        var originalTotal = original.TotalRecordCount;
        int total;

        if (originalTotal <= 0)
        {
            total = originalTotal; // o cliente nao pediu total
        }
        else if (plan.Applied)
        {
            // Buscamos do inicio: se o Jellyfin devolveu menos que o pedido, temos tudo.
            var fetchedAll = plan.Fetched == 0 || rawCount < plan.Fetched;
            total = fetchedAll
                ? dedupedCount
                : dedupedCount + Math.Max(0, originalTotal - rawCount);
        }
        else if (plan.Start == 0 && plan.Limit is null)
        {
            total = dedupedCount; // lista completa
        }
        else
        {
            // Pagina isolada (sem ampliar): estimativa pelo que foi removido.
            total = Math.Max(dedupedCount, originalTotal - (rawCount - dedupedCount));
        }

        var startIndex = plan.Applied ? plan.Start : original.StartIndex;
        holder.Value = new QueryResult<BaseItemDto>(startIndex, total, page);
    }

    /// <summary>Se algo falhar depois de ampliar a consulta, devolve ao menos a janela original.</summary>
    private static void TryTrim(ActionExecutedContext executed, Plan plan)
    {
        try
        {
            if (!plan.Applied)
            {
                return;
            }

            if (executed.Result is ObjectResult { Value: QueryResult<BaseItemDto> result } holder)
            {
                var list = (result.Items ?? Array.Empty<BaseItemDto>())
                    .Skip(plan.Start)
                    .Take(plan.Limit ?? int.MaxValue)
                    .ToList();
                holder.Value = new QueryResult<BaseItemDto>(plan.Start, result.TotalRecordCount, list);
            }
        }
        catch
        {
            // Ultimo recurso: nada a fazer.
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

        if (path.EndsWith("/Items/Resume", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/UserItems/Resume", StringComparison.OrdinalIgnoreCase))
        {
            return Kind.Resume;
        }

        // Next Up de uma serie especifica (pagina da serie) nao entra.
        if (path.EndsWith("/Shows/NextUp", StringComparison.OrdinalIgnoreCase)
            && !request.Query.ContainsKey("seriesId"))
        {
            return Kind.NextUp;
        }

        return Kind.None;
    }

    private static Guid GetUserId(ActionExecutingContext context)
    {
        var http = context.HttpContext;
        string? raw = http.User.FindFirst("Jellyfin-UserId")?.Value;

        if (raw is null && context.ActionArguments.TryGetValue("userId", out var arg))
        {
            raw = arg?.ToString();
        }

        raw ??= http.Request.Query["userId"].FirstOrDefault()
            ?? http.Request.RouteValues["userId"]?.ToString();

        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }

    private static PluginConfiguration GetConfig()
        => Plugin.Instance?.Configuration ?? new PluginConfiguration();
}
