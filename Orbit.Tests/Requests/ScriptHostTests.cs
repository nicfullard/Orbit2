using Orbit.Scripting;

namespace Orbit.Tests.Requests;

/// <summary>Request action scripts (spec §6.20): what a script sees, how its output and failures come back, and the time limit.</summary>
public class ScriptHostTests
{
    private static readonly ScriptHost Host = new();

    private static ScriptRunContext Context(Dictionary<string, string?>? inputs = null, Dictionary<string, string?>? values = null) => new()
    {
        Request = new ScriptRequestInfo { Number = "R-26-00007", Title = "New product code", Requester = new ScriptRequester { Name = "Ann Lee", Email = "ann@example.com" }, Department = "Product", Category = "Products", Flow = "Product code" },
        Values = values ?? new Dictionary<string, string?> { ["details.description"] = "Blue widget" },
        Inputs = inputs ?? new Dictionary<string, string?> { ["code"] = "BW-100" },
        Connections = new Dictionary<string, DbConnectionSpec>()
    };

    /// <summary>REQ-022: a script sees the request, its inputs and values; what it logs and returns is its output; a compile error names the line.</summary>
    [Fact]
    public async Task A_script_sees_the_request_and_reports_its_output()
    {
        var outcome = await Host.RunAsync("""
            Log($"{Request.Number} by {Request.Requester.Name}");
            Log(Input("code") + " / " + Value("details.description"));
            return Inputs.Count + Values.Count;
            """, Context(), TimeSpan.FromSeconds(30));
        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal("R-26-00007 by Ann Lee\nBW-100 / Blue widget\n2", outcome.Output!.Replace("\r\n", "\n"));
        Assert.Null(outcome.Error);

        Assert.Empty(Host.Check("var x = 1;\nLog(x.ToString());"));
        var errors = Host.Check("var x = 1;\nLog(y);");
        Assert.Single(errors);
        Assert.StartsWith("line 2:", errors[0]);
        var refused = await Host.RunAsync("Log(y);", Context(), TimeSpan.FromSeconds(30));
        Assert.False(refused.Ok);
        Assert.Contains("doesn't compile", refused.Error);
    }

    /// <summary>REQ-022: an exception fails the run with its type and message, keeping what was logged; an unknown connection is refused by name.</summary>
    [Fact]
    public async Task A_script_that_throws_fails_with_its_message()
    {
        var outcome = await Host.RunAsync("""
            Log("before");
            throw new InvalidOperationException("no such product");
            """, Context(), TimeSpan.FromSeconds(30));
        Assert.False(outcome.Ok);
        Assert.Equal("before", outcome.Output);
        Assert.Equal("InvalidOperationException: no such product", outcome.Error);

        var missing = await Host.RunAsync("""var db = Connections.Open("erp");""", Context(), TimeSpan.FromSeconds(30));
        Assert.False(missing.Ok);
        Assert.Contains("No connection named \"erp\"", missing.Error);
    }

    /// <summary>REQ-022: a script that outruns its time limit is reported as failed, with what it logged so far.</summary>
    [Fact]
    public async Task A_script_that_runs_too_long_is_failed()
    {
        var outcome = await Host.RunAsync("""
            Log("started");
            await Task.Delay(TimeSpan.FromSeconds(30), Ct);
            Log("never");
            """, Context(), TimeSpan.FromMilliseconds(500));
        Assert.False(outcome.Ok);
        Assert.Equal("started", outcome.Output);
        Assert.Contains("did not finish within", outcome.Error);
    }
}
