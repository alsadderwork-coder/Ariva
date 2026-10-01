using NHibernate.Linq;

namespace Ariva.Infra.Services.Foundation;

/// <summary>
/// Base class for entity services, ported from AMAN with the same method signatures. AMAN's lookup validation
/// service and cache are not ported yet; services that need FusionCache take it in their own constructor (ARV-008).
/// </summary>
internal abstract class SvcBase(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider) : SvcDb(unitOfWork)
{
    protected ICurrentUser CurrentUser { get; } = currentUser;

    protected TimeProvider TimeProvider { get; } = timeProvider;

    /// <summary>UTC now from the injected clock, so tests control time.</summary>
    protected DateTime UtcNow => TimeProvider.GetUtcNow().UtcDateTime;

    /// <summary>Runs the request's specification and returns its errors, if any (AMAN signature).</summary>
    protected static async Task<Result> ValidateRequestAsync<TRequest>(
        TRequest request,
        Func<TRequest, Task<Result<bool>>> validator,
        CancellationToken ctx = default)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ctx.ThrowIfCancellationRequested();

        var valid = await validator(request);
        return valid.HasErrors ? Result.Error(valid.ErrorMessages) : Result.Return();
    }

    /// <summary>
    /// Count and page in one round trip with NHibernate futures (AMAN signature). Page index is 1-based; size is
    /// clamped to 1..500 so a caller cannot request the whole table (CWE-400).
    /// </summary>
    protected static async Task<PagedData<TViewModel>> ExecutePagedQueryAsync<TEntity, TViewModel>(
        IQueryable<TEntity> countQuery,
        IQueryable<TViewModel> dataQuery,
        int pageIndex,
        int pageSize,
        CancellationToken ctx = default) where TEntity : IDomain
    {
        pageIndex = Math.Max(1, pageIndex);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var totalCountFuture = countQuery.ToFutureValue(query => query.Count());
        var dataFuture = dataQuery
            .Skip((pageIndex - 1) * pageSize)
            .Take(pageSize)
            .ToFuture();

        var data = (await dataFuture.GetEnumerableAsync(ctx)).ToList();
        var totalCount = await totalCountFuture.GetValueAsync(ctx);

        return new PagedData<TViewModel>
        {
            Data = data,
            TotalCount = totalCount,
            PageIndex = pageIndex,
            PageSize = pageSize
        };
    }

    protected const int MaxPageSize = 500;
}
