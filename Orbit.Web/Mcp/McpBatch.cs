using ModelContextProtocol;
using Orbit.Application;

namespace Orbit.Mcp;

/// <summary>What a batch tool did with one of its items, by its position in the call.</summary>
/// <param name="Item">The item as the caller named it - an asset id or number, or a new asset's name - to match it without counting.</param>
/// <param name="Result">What the single tool would have said about it, trimmed to a summary; null when it wasn't saved.</param>
/// <param name="Error">Why it wasn't saved; null when it was.</param>
public sealed record BatchItem(int Index, string? Item, bool Ok, object? Result = null, string? Error = null);

/// <summary>A batch tool's reply: the counts, then every item in the order given.</summary>
public sealed record BatchOutcome(int Total, int Succeeded, int Failed, int NotAttempted, IReadOnlyList<BatchItem> Items);

/// <summary>
/// The loop behind the batch tools - create_assets, update_assets, record_asset_checks (spec §6.19, §7.1). Each item goes through
/// the same code as the single tool and is saved or refused on its own: a refusal is reported against the item and the next one
/// is tried, so the caller resubmits only what was refused. An unexpected failure (a lost database connection, say) would fail
/// every later item the same way, so it stops the call, and the reply still says which items were saved and which weren't tried.
/// </summary>
public static class McpBatch
{
    /// <summary>The most items one call takes - enough for a sheet of assets, few enough that a reply stays readable.</summary>
    public const int MaxItems = 100;

    /// <param name="label">How the caller named the item, echoed in its result.</param>
    /// <param name="apply">Saves one item and returns what to report about it; throws an <see cref="OrbitException"/> or an
    /// <see cref="McpException"/> to refuse it.</param>
    /// <param name="afterEach">Runs after every item attempted, saved or not - the tools discard what a refused item left unsaved.</param>
    /// <param name="unexpected">Told about a failure that stopped the call, with the index of the item it stopped at.</param>
    public static async Task<BatchOutcome> RunAsync<T>(IReadOnlyList<T?>? items, Func<T, string?> label, Func<T, Task<object>> apply,
        Action afterEach, Action<int, Exception> unexpected, CancellationToken ct) where T : class
    {
        if (items is null || items.Count == 0) throw new McpException("items is empty; pass at least one.");
        if (items.Count > MaxItems)
            throw new McpException($"At most {MaxItems} items per call; got {items.Count}. Split them over several calls.");

        var results = new List<BatchItem>(items.Count);
        var notAttempted = 0;
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];
            var name = item is null ? null : Label(label(item));
            try
            {
                if (item is null) throw new McpException("The item is empty.");
                results.Add(new BatchItem(i, name, true, await apply(item)));
            }
            catch (Exception ex) when (ex is OrbitException or McpException)
            {
                results.Add(new BatchItem(i, name, false, Error: ex.Message));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unexpected(i, ex);
                results.Add(new BatchItem(i, name, false, Error: "Not saved: an unexpected error, which has been logged. The items after it weren't attempted."));
                for (var j = i + 1; j < items.Count; j++)
                    results.Add(new BatchItem(j, items[j] is T rest ? Label(label(rest)) : null, false,
                        Error: $"Not attempted: the call stopped at item {i}. Send it again."));
                notAttempted = items.Count - i - 1;
                break;
            }
            finally
            {
                afterEach();
            }
        }

        var succeeded = results.Count(r => r.Ok);
        return new BatchOutcome(items.Count, succeeded, results.Count - succeeded - notAttempted, notAttempted, results);
    }

    private static string? Label(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
