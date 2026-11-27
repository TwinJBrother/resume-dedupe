using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Episode = MediaBrowser.Controller.Entities.TV.Episode;

namespace Jellyfin.Plugin.ResumeDedupe;

/// <summary>
/// Continue Watching (/UserItems/Resume): um episodio por serie, o mais recente primeiro.
/// Next Up (/Shows/NextUp): esconde series que ja tem episodio em andamento.
///
/// Projeto:
///  - Filtro MVC singleton; trabalha com objetos, nunca com bytes (gzip/proxy nao interferem).
///  - Fast path sincrono: pedido que nao e Resume/NextUp custa um teste de caractere e devolve next().
///  - Paginacao exata (Wholphin/Moonfin paginam com startIndex+limit): pede ao Jellyfin so
///    limite + duplicatas conhecidas, deduplica e devolve a janela pedida.
///  - Uma consulta leve por usuario (sem DTOs), em cache de 2 s, compartilhada pelos dois endpoints.
///  - Premissa verificada: o Jellyfin lista o Continue Watching do mais recente ao mais antigo. Toda resposta e conferida
///    pela data de reproducao; se vier fora de ordem, o episodio mais recente assume a vaga e o plugin passa a ler a lista
///    inteira (com aviso no log). Na ordem esperada nao custa nada alem de uma comparacao por item.
///  - Qualquer falha devolve a resposta original; 5 falhas seguidas pausam o plugin por 10 min.
/// </summary>
public sealed class ResumeDedupeFilter(ILogger<ResumeDedupeFilter> log, IServiceProvider services)
    : IAsyncActionFilter
{
    private const int Pad = 10;               // folga do Continue Watching (itens Series/filmes fora da contagem)
    private const int MaxFetch = 500;         // teto de itens pedidos ao Jellyfin
    private const int MaxScan = 2000;         // teto de episodios em andamento lidos por usuario
    private const int TripAfter = 5;
    private const long TtlMs = 2_000;
    private const long PauseMs = 600_000;

    private static readonly ConcurrentDictionary<Guid, Entry> Cache = new();

    private static int _fails, _firstResume, _firstNextUp, _loggedLoad, _loggedType, _loggedNoUser, _unsorted;
    private static long _pausedUntil;

    private enum Kind : byte
    {
        None,
        Resume,
        NextUp
    }

    /// <summary>O que o cliente pediu e o que vamos esconder. Kind.None = nao mexer.</summary>
    private readonly record struct Plan(Kind Kind, int Start, int? Limit, bool Widened, HashSet<Guid>? Hidden);

    /// <summary>Series com episodio em andamento e quantos episodios sobram alem de um por serie.</summary>
    private sealed record Info(HashSet<Guid> Series, int Extra);

    private sealed class Entry(long at, Func<Info?> load)
    {
        public long At { get; } = at;

        public Lazy<Info?> Value { get; } = new(load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    // ------------------------------------------------------------------ ENTRADA

    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var kind = Classify(context.HttpContext.Request);
        return kind == Kind.None || Environment.TickCount64 < Volatile.Read(ref _pausedUntil)
            ? next()
            : Run(context, next, kind);
    }

    private async Task Run(ActionExecutingContext context, ActionExecutionDelegate next, Kind kind)
    {
        Plan plan;
        try
        {
            plan = Prepare(context, kind);
        }
        catch (Exception ex)
        {
            Fail(ex, "falha ao preparar a consulta");
            plan = default;
        }

        var executed = await next().ConfigureAwait(false);
        if (plan.Kind == Kind.None || executed.Exception is not null)
        {
            return;
        }

        try
        {
            if (executed.Result is ObjectResult { Value: QueryResult<BaseItemDto> result } holder)
            {
                Reshape(holder, result, plan);
                if (Volatile.Read(ref _fails) != 0)
                {
                    Interlocked.Exchange(ref _fails, 0);
                }
            }
            else if (Interlocked.Exchange(ref _loggedType, 1) == 0)
            {
                log.LogWarning(
                    "ResumeDedupe: resposta inesperada ({Type}); nada foi alterado",
                    (executed.Result as ObjectResult)?.Value?.GetType().FullName ?? executed.Result?.GetType().FullName);
            }
        }
        catch (Exception ex)
        {
            Fail(ex, "falha ao processar");
            Trim(executed.Result, plan);
        }
    }

    // --------------------------------------------------------------- ANTES DA ACAO

    /// <summary>
    /// Decide o que esconder e, se o cliente pagina, amplia a consulta para que depois de remover
    /// as duplicatas a janela (startIndex/limit) continue cheia.
    /// </summary>
    private Plan Prepare(ActionExecutingContext context, Kind kind)
    {
        if (kind == Kind.Resume && !WantsVideo(context.HttpContext.Request.Query))
        {
            return default;
        }

        var args = context.ActionArguments;

        // A escolha do episodio mais recente usa a data de reproducao (UserData); se o cliente a desligou, religa.
        if (kind == Kind.Resume && args.TryGetValue("enableUserData", out var userData) && userData is false)
        {
            args["enableUserData"] = true;
        }

        var start = Math.Max(0, ReadInt(args, "startIndex") ?? 0);
        var limit = ReadInt(args, "limit");
        if (limit <= 0)
        {
            limit = null; // o Jellyfin trata 0 como "sem limite"
        }

        // Continue Watching de lista completa: so deduplicar. Nem precisa saber quem e o usuario.
        if (kind == Kind.Resume && limit is null && start == 0)
        {
            return new Plan(kind, 0, null, false, null);
        }

        // Next Up precisa das series em andamento do usuario; Resume so as usa para dimensionar a ampliacao
        // (sem usuario identificado ele ainda deduplica, com ampliacao estimada).
        var hasUser = TryGetUser(context, out var user);
        if (!hasUser && Interlocked.Exchange(ref _loggedNoUser, 1) == 0)
        {
            log.LogWarning("ResumeDedupe: usuario do pedido nao identificado (sem token ou userId diferente do token); Next Up nao sera filtrado");
        }

        var info = hasUser ? GetInfo(user) : null;
        HashSet<Guid>? hidden = null;
        if (kind == Kind.NextUp)
        {
            hidden = info?.Series;
            if (hidden is not { Count: > 0 })
            {
                return default; // nada a esconder: o pedido passa intacto
            }
        }

        var plain = new Plan(kind, start, limit, false, hidden);
        if (limit is null && start == 0)
        {
            return plain; // lista completa: nada a ampliar
        }

        // Quantos itens, no maximo, serao removidos (-1 = desconhecido).
        var extra = kind == Kind.NextUp ? hidden!.Count : info?.Extra ?? -1;
        if (extra == 0)
        {
            return plain;
        }

        if (limit is null)
        {
            // Offset sem limite: buscar tudo do inicio e cortar depois.
            if (!args.ContainsKey("startIndex"))
            {
                return plain;
            }

            args["startIndex"] = 0;
            return plain with { Widened = true };
        }

        // Next Up: as series escondidas sao um limite exato (um item por serie), entao sem folga:
        // o Jellyfin calcula o proximo episodio de cada serie pedida, e esse e o calculo mais caro.
        var need = (long)start + limit.Value;
        var wide = kind == Kind.Resume && Volatile.Read(ref _unsorted) != 0
            ? MaxFetch // ordem nao confiavel: ler a lista inteira para escolher o episodio certo
            : Math.Min(extra > 0 ? need + extra + (kind == Kind.NextUp ? 0 : Pad) : need * 4 + 30, MaxFetch);
        if (wide < need || !args.ContainsKey("limit"))
        {
            return plain; // pagina muito funda: sem ampliar
        }

        args["limit"] = (int)wide;
        if (start > 0 && args.ContainsKey("startIndex"))
        {
            args["startIndex"] = 0;
        }

        return plain with { Widened = true };
    }

    // ------------------------------------------------------------------ DEPOIS DA ACAO

    private void Reshape(ObjectResult holder, QueryResult<BaseItemDto> result, in Plan plan)
    {
        var raw = result.Items as IReadOnlyList<BaseItemDto> ?? result.Items?.ToList() ?? new List<BaseItemDto>();
        var kept = new List<BaseItemDto>(raw.Count);
        var slots = plan.Kind == Kind.Resume ? new Dictionary<Guid, int>() : null;
        DateTime? previous = null;

        foreach (var item in raw)
        {
            if (slots is null)
            {
                if (item.SeriesId is { } sid && plan.Hidden!.Contains(sid))
                {
                    continue;
                }
            }
            else
            {
                // Verifica a premissa (mais recente primeiro) em toda resposta, sem custo relevante.
                if (item.UserData?.LastPlayedDate is { } played)
                {
                    if (previous is { } before && played > before)
                    {
                        NoteUnsorted();
                    }

                    previous = played;
                }

                var key = KeyOf(item);
                if (key != Guid.Empty)
                {
                    if (slots.TryGetValue(key, out var slot))
                    {
                        // Ja temos esta serie. Normalmente o primeiro e o mais recente (o Jellyfin ordena por data
                        // de reproducao); se a data disser o contrario, o mais recente assume a vaga.
                        if (IsNewer(item, kept[slot]))
                        {
                            kept[slot] = item;
                            NoteUnsorted();
                        }

                        continue;
                    }

                    slots[key] = kept.Count;
                }
            }

            kept.Add(item);
        }

        var removed = raw.Count - kept.Count;
        if (removed == 0 && !plan.Widened)
        {
            return; // nada mudou
        }

        var page = kept;
        if (plan.Widened)
        {
            var skip = Math.Min(plan.Start, kept.Count);
            page = kept.GetRange(skip, Math.Min(plan.Limit ?? int.MaxValue, kept.Count - skip));
        }

        // Total estimado: o total do servidor menos o que removemos (exato se buscamos tudo).
        var original = result.TotalRecordCount;
        var total = original > 0
            ? Math.Max(original - removed, (plan.Widened ? plan.Start : 0) + page.Count)
            : original;

        if (removed > 0)
        {
            Hit(plan.Kind, raw.Count, page.Count);
        }

        holder.Value = new QueryResult<BaseItemDto>(plan.Widened ? plan.Start : result.StartIndex, total, page);
    }

    /// <summary>Se algo falhar depois de ampliar a consulta, devolve ao menos a janela original.</summary>
    private static void Trim(IActionResult? result, in Plan plan)
    {
        if (!plan.Widened || result is not ObjectResult { Value: QueryResult<BaseItemDto> r } holder)
        {
            return;
        }

        try
        {
            var items = (r.Items ?? Array.Empty<BaseItemDto>())
                .Skip(plan.Start)
                .Take(plan.Limit ?? int.MaxValue)
                .ToList();
            holder.Value = new QueryResult<BaseItemDto>(plan.Start, r.TotalRecordCount, items);
        }
        catch
        {
            // Ultimo recurso: nada a fazer.
        }
    }

    private static bool IsNewer(BaseItemDto candidate, BaseItemDto current)
        => candidate.UserData?.LastPlayedDate is { } a && current.UserData?.LastPlayedDate is { } b && a > b;

    /// <summary>
    /// O Continue Watching veio fora da ordem de reproducao: a premissa do plugin falhou. Dai em diante
    /// le a lista inteira (ate MaxFetch) para sempre escolher o episodio mais recente de cada serie.
    /// </summary>
    private void NoteUnsorted()
    {
        if (Interlocked.Exchange(ref _unsorted, 1) == 0)
        {
            log.LogWarning("ResumeDedupe: o Continue Watching veio fora da ordem de reproducao; lendo a lista inteira e usando a data de reproducao.");
        }
    }

    /// <summary>Episodio/temporada contam para a serie; um item Series (Jellyfin 12.2+) conta para si mesmo.</summary>
    private static Guid KeyOf(BaseItemDto item) => item.Type switch
    {
        BaseItemKind.Episode or BaseItemKind.Season => item.SeriesId ?? Guid.Empty,
        BaseItemKind.Series => item.Id,
        _ => Guid.Empty
    };

    // ----------------------------------------------------------- CONSULTA DIRETA

    private Info? GetInfo(Guid user)
    {
        var now = Environment.TickCount64;
        return Cache.TryGetValue(user, out var entry) && now - entry.At <= TtlMs
            ? entry.Value.Value
            : Refresh(user, now);
    }

    // Separado de GetInfo para que o caminho de acerto do cache nao aloque closures.
    private Info? Refresh(Guid user, long now)
    {
        var entry = Cache.AddOrUpdate(
            user,
            _ => new Entry(now, () => Load(user)),
            (_, old) => now - old.At > TtlMs ? new Entry(now, () => Load(user)) : old);

        if (Cache.Count > 64)
        {
            foreach (var (key, stale) in Cache)
            {
                if (now - stale.At > 60_000)
                {
                    Cache.TryRemove(key, out _);
                }
            }
        }

        return entry.Value.Value;
    }

    /// <summary>Mesma consulta do Continue Watching, mas sem DTOs: so as series e a contagem.</summary>
    private Info? Load(Guid userId)
    {
        try
        {
            var library = services.GetService<ILibraryManager>();
            var user = services.GetService<IUserManager>()?.GetUserById(userId);
            if (library is null || user is null)
            {
                return null;
            }

            var query = new InternalItemsQuery(user)
            {
                IsResumable = true,
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Episode],
                IsVirtualItem = false,
                CollapseBoxSetItems = false,
                IncludeOwnedItems = true,
                EnableTotalRecordCount = false,
                DtoOptions = new DtoOptions(false),
                Limit = MaxScan
            };

            var series = new HashSet<Guid>();
            var extra = 0;
            foreach (var item in library.GetItemList(query))
            {
                if (item is Episode { SeriesId: var id } && id != Guid.Empty && !series.Add(id))
                {
                    extra++;
                }
            }

            return new Info(series, extra);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _loggedLoad, 1) == 0)
            {
                log.LogWarning(ex, "ResumeDedupe: nao foi possivel consultar os episodios em andamento; modo reduzido");
            }

            return null;
        }
    }

    // --------------------------------------------------------------------- APOIO

    private static Kind Classify(HttpRequest request)
    {
        var path = request.Path.Value;
        if (path is null || path.Length < 13)
        {
            return Kind.None;
        }

        // Quase todo pedido da API acaba aqui: so rotas terminadas em "e" (Resume) ou "p" (NextUp) seguem.
        switch (path[^1] | 0x20)
        {
            case 'e' when path.EndsWith("/Items/Resume", StringComparison.OrdinalIgnoreCase)
                       || path.EndsWith("/UserItems/Resume", StringComparison.OrdinalIgnoreCase):
                return Kind.Resume;

            // Next Up de uma serie especifica (pagina da serie) nao entra.
            case 'p' when path.EndsWith("/Shows/NextUp", StringComparison.OrdinalIgnoreCase)
                       && !request.Query.ContainsKey("seriesId"):
                return Kind.NextUp;

            default:
                return Kind.None;
        }
    }

    /// <summary>
    /// Usuario do pedido. O token manda; se o pedido nomeia outro usuario (admin vendo outra conta)
    /// nao mexemos. Com chave de API (sem token de usuario) vale o userId do pedido.
    /// </summary>
    private static bool TryGetUser(ActionExecutingContext context, out Guid user)
    {
        var asked = context.ActionArguments.TryGetValue("userId", out var arg) && arg is Guid g ? g : Guid.Empty;

        if (Guid.TryParse(context.HttpContext.User.FindFirst("Jellyfin-UserId")?.Value, out var self) && self != Guid.Empty)
        {
            user = self;
            return asked == Guid.Empty || asked == self;
        }

        user = asked;
        return asked != Guid.Empty;
    }

    /// <summary>Continue Listening/Reading (mediaTypes sem Video) nao tem episodios a deduplicar.</summary>
    private static bool WantsVideo(IQueryCollection query)
    {
        if (!query.TryGetValue("mediaTypes", out var media))
        {
            return true;
        }

        var any = false;
        foreach (var value in media)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            any = true;
            if (value.Contains("Video", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return !any;
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

    private void Hit(Kind kind, int before, int after)
    {
        if (Interlocked.Exchange(ref (kind == Kind.Resume ? ref _firstResume : ref _firstNextUp), 1) == 0)
        {
            log.LogInformation("ResumeDedupe: {Kind} filtrado ({Before} -> {After} itens). Funcionando.", kind, before, after);
        }
    }

    private void Fail(Exception ex, string what)
    {
        log.LogWarning(ex, "ResumeDedupe: {What}; resposta original mantida", what);
        if (Interlocked.Increment(ref _fails) < TripAfter)
        {
            return;
        }

        Interlocked.Exchange(ref _fails, 0);
        Volatile.Write(ref _pausedUntil, Environment.TickCount64 + PauseMs);
        log.LogError("ResumeDedupe: falhas repetidas; pausado por 10 minutos (a API do Jellyfin segue normal)");
    }
}
