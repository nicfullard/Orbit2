using System.Text.RegularExpressions;
using Orbit.Data.Entities;

namespace Orbit.Application.Requests;

/// <summary>One value a later step may reference (§6.20): its token name ("details.description") and a label for the picker.</summary>
public sealed record RequestToken(string Name, string Label)
{
    /// <summary>The token as written in a template: {{details.description}}.</summary>
    public string Text => "{{" + Name + "}}";
}

/// <summary>
/// Tokens (spec §6.20): how a step refers to values gathered earlier - <c>{{step-key.field-key}}</c> for a form's answers,
/// <c>{{step-key.number}}</c> and friends for a task step, <c>{{step-key.outcome}}</c> for an approval, <c>{{request.number}}</c> and
/// the rest for the request itself. A step may only use the tokens of the steps it depends on, directly or through others, so a value
/// is always known by the time it is used.
/// </summary>
public static partial class RequestTokenRules
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*)\s*\}\}")]
    private static partial Regex TokenPattern();

    /// <summary>The request's own values, available to every step.</summary>
    public static readonly IReadOnlyList<RequestToken> RequestTokens =
    [
        new("request.number", "Request: number"),
        new("request.title", "Request: title"),
        new("request.requester", "Request: who logged it"),
        new("request.requester.email", "Request: their email"),
        new("request.department", "Request: department"),
        new("request.category", "Request: category"),
        new("request.flow", "Request: flow")
    ];

    /// <summary>REQ-012: the distinct token names in a template, in order, lower-cased; empty for no template.</summary>
    public static IReadOnlyList<string> Parse(string? template) =>
        template is null ? [] : TokenPattern().Matches(template).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().ToList();

    /// <summary>REQ-012: the tokens a step gives the steps after it. Needs the step's fields loaded.</summary>
    public static IReadOnlyList<RequestToken> Offered(RequestFlowStep step) => step.Kind switch
    {
        RequestStepKind.Form => step.Fields.OrderBy(f => f.DisplayOrder)
            .Select(f => new RequestToken($"{step.Key}.{f.Key}", $"{step.Title}: {f.Prompt}")).ToList(),
        RequestStepKind.Task =>
        [
            new($"{step.Key}.number", $"{step.Title}: task number"),
            new($"{step.Key}.title", $"{step.Title}: task title"),
            new($"{step.Key}.assignees", $"{step.Title}: assignees"),
            new($"{step.Key}.completed-by", $"{step.Title}: completed by"),
            new($"{step.Key}.completed-on", $"{step.Title}: completed on")
        ],
        RequestStepKind.Approval =>
        [
            new($"{step.Key}.outcome", $"{step.Title}: outcome (Accepted or Declined)"),
            new($"{step.Key}.comments", $"{step.Title}: the approvers' comments")
        ],
        RequestStepKind.Action => [new($"{step.Key}.output", $"{step.Title}: the script's output")],
        _ => []
    };

    /// <summary>REQ-012: everything a step may reference: the request's tokens, then each predecessor's, in the flow's order.</summary>
    public static IReadOnlyList<RequestToken> Available(IEnumerable<RequestFlowStep> predecessors) =>
        [.. RequestTokens, .. predecessors.OrderBy(s => s.DisplayOrder).ThenBy(s => s.Title).SelectMany(Offered)];

    /// <summary>REQ-012: refuses a template that names a token the step can't use - unknown, or of a step it doesn't depend on.</summary>
    public static void Validate(string? template, IReadOnlySet<string> available, string what)
    {
        foreach (var token in Parse(template))
        {
            if (!available.Contains(token))
                throw new ValidationException($"{what} uses {{{{{token}}}}}, which isn't a value of the request or of a step this step depends on.");
        }
    }

    /// <summary>REQ-012: the template with each token replaced by its value; a token with no value becomes nothing.</summary>
    public static string Render(string? template, Func<string, string?> value) =>
        template is null ? string.Empty : TokenPattern().Replace(template, m => value(m.Groups[1].Value.ToLowerInvariant()) ?? string.Empty);

    public static string Render(string? template, IReadOnlyDictionary<string, string?> values) =>
        Render(template, token => values.GetValueOrDefault(token));

    /// <summary>REQ-012: a web address with its tokens filled in, each value escaped so a space or an ampersand in an answer can't break the address.</summary>
    public static string RenderUrl(string? template, IReadOnlyDictionary<string, string?> values) =>
        Render(template, token => values.GetValueOrDefault(token) is { } v ? Uri.EscapeDataString(v) : null);
}
