using ModelContextProtocol;
using Orbit.Application;
using Orbit.Mcp;

namespace Orbit.Tests.Mcp;

/// <summary>
/// AST-026: the batch asset tools' loop (spec §6.19, §7.1) - each item saved or refused on its own and reported by index, the unit
/// of work reset after every item, and an unexpected failure stopping the call with the rest reported as not attempted.
/// </summary>
public class McpBatchTests
{
    private sealed record Item(string Name);

    private static Item[] Items(params string[] names) => names.Select(n => new Item(n)).ToArray();

    private static Task<BatchOutcome> Run(IReadOnlyList<Item?>? items, Func<Item, Task<object>> apply, Action? afterEach = null,
        Action<int, Exception>? unexpected = null) =>
        McpBatch.RunAsync(items, i => i.Name, apply, afterEach ?? (() => { }), unexpected ?? ((_, _) => { }), CancellationToken.None);

    private static Task<object> Saved(Item i) => Task.FromResult<object>($"saved {i.Name}");

    [Fact]
    public async Task Every_item_is_saved_and_reported_in_order()
    {
        var outcome = await Run(Items("a", "b", "c"), Saved);

        Assert.Equal((3, 3, 0, 0), (outcome.Total, outcome.Succeeded, outcome.Failed, outcome.NotAttempted));
        Assert.Equal([0, 1, 2], outcome.Items.Select(r => r.Index));
        Assert.Equal(["a", "b", "c"], outcome.Items.Select(r => r.Item));
        Assert.All(outcome.Items, r => { Assert.True(r.Ok); Assert.Null(r.Error); });
        Assert.Equal("saved b", outcome.Items[1].Result);
    }

    [Fact]
    public async Task A_refused_item_is_reported_and_the_rest_are_still_saved()
    {
        var outcome = await Run(Items("a", "dup", "b", "bad", "c"), i => i.Name switch
        {
            "dup" => throw new ValidationException("Asset number FA-1 is already taken."),
            "bad" => throw new McpException("purchaseDate must be a date like 2026-09-30; got \"soon\"."),
            _ => Saved(i)
        });

        Assert.Equal((5, 3, 2, 0), (outcome.Total, outcome.Succeeded, outcome.Failed, outcome.NotAttempted));
        Assert.Equal([true, false, true, false, true], outcome.Items.Select(r => r.Ok));
        Assert.Equal("Asset number FA-1 is already taken.", outcome.Items[1].Error);
        Assert.Null(outcome.Items[1].Result);
        Assert.Equal("purchaseDate must be a date like 2026-09-30; got \"soon\".", outcome.Items[3].Error);
    }

    [Fact]
    public async Task Forbidden_and_not_found_are_refusals_too()
    {
        var outcome = await Run(Items("a", "b"), i => i.Name == "a"
            ? throw new ForbiddenException("You don't have permission to register assets in this department.")
            : throw new NotFoundException("Asset not found."));

        Assert.Equal((2, 0, 2, 0), (outcome.Total, outcome.Succeeded, outcome.Failed, outcome.NotAttempted));
        Assert.Equal(["You don't have permission to register assets in this department.", "Asset not found."], outcome.Items.Select(r => r.Error));
    }

    [Fact]
    public async Task A_null_item_is_refused_on_its_own()
    {
        var outcome = await Run([new Item("a"), null, new Item("b")], Saved);

        Assert.Equal((3, 2, 1, 0), (outcome.Total, outcome.Succeeded, outcome.Failed, outcome.NotAttempted));
        Assert.False(outcome.Items[1].Ok);
        Assert.Null(outcome.Items[1].Item);
        Assert.Equal("The item is empty.", outcome.Items[1].Error);
    }

    [Fact]
    public async Task A_blank_label_is_left_out_and_a_label_is_trimmed()
    {
        var outcome = await Run(Items("  ", " FA-1 "), Saved);

        Assert.Equal([null, "FA-1"], outcome.Items.Select(r => r.Item));
    }

    [Fact]
    public async Task An_unexpected_failure_stops_the_call_and_the_rest_are_not_attempted()
    {
        var attempted = new List<string>();
        (int Index, Exception Error)? logged = null;
        var outcome = await Run(Items("a", "b", "c", "d"), i =>
        {
            attempted.Add(i.Name);
            return i.Name == "b" ? throw new InvalidOperationException("connection lost") : Saved(i);
        }, unexpected: (index, ex) => logged = (index, ex));

        Assert.Equal(["a", "b"], attempted);
        Assert.Equal((4, 1, 1, 2), (outcome.Total, outcome.Succeeded, outcome.Failed, outcome.NotAttempted));
        Assert.Equal([0, 1, 2, 3], outcome.Items.Select(r => r.Index));
        Assert.Equal(["a", "b", "c", "d"], outcome.Items.Select(r => r.Item));
        Assert.False(outcome.Items[1].Ok);
        Assert.DoesNotContain("connection lost", outcome.Items[1].Error); // the detail goes to the log, not the caller
        Assert.All(outcome.Items.Skip(2), r => { Assert.False(r.Ok); Assert.StartsWith("Not attempted", r.Error); });
        Assert.Equal(1, logged?.Index);
        Assert.Equal("connection lost", logged?.Error.Message);
    }

    [Fact]
    public async Task The_unit_of_work_is_reset_after_every_item_attempted()
    {
        var resets = 0;
        await Run(Items("a", "bad", "c"), i => i.Name == "bad" ? throw new ValidationException("no") : Saved(i), () => resets++);
        Assert.Equal(3, resets);

        resets = 0;
        await Run(Items("a", "boom", "c"), i => i.Name == "boom" ? throw new InvalidOperationException() : Saved(i), () => resets++);
        Assert.Equal(2, resets); // the item that stopped the call is reset too; the one after it was never attempted
    }

    [Fact]
    public async Task Cancellation_is_not_reported_as_an_item_failure()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Run(Items("a", "b"), i => i.Name == "b" ? throw new OperationCanceledException() : Saved(i)));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            McpBatch.RunAsync(Items("a"), i => i.Name, Saved, () => { }, (_, _) => { }, cts.Token));
    }

    [Fact]
    public async Task No_items_or_too_many_are_refused_before_anything_is_saved()
    {
        var calls = 0;
        Task<object> Count(Item i) { calls++; return Saved(i); }

        await Assert.ThrowsAsync<McpException>(() => Run(null, Count));
        await Assert.ThrowsAsync<McpException>(() => Run([], Count));
        var tooMany = await Assert.ThrowsAsync<McpException>(() =>
            Run(Enumerable.Range(0, McpBatch.MaxItems + 1).Select(n => new Item($"n{n}")).ToArray(), Count));
        Assert.Contains("At most 100", tooMany.Message);
        Assert.Equal(0, calls);

        var full = await Run(Enumerable.Range(0, McpBatch.MaxItems).Select(n => new Item($"n{n}")).ToArray(), Count);
        Assert.Equal(100, full.Succeeded);
    }
}
