using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Actions;

/// <summary>Writing an action (spec §6.20): its name, where it runs, its parameters (one per line) and its script, compiled when saved.</summary>
public class EditModel(RequestActionService actions, IOptions<ActionOptions> actionOptions) : OrbitPageModel
{
    public sealed class ActionForm
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public ActionRunsOn RunsOn { get; set; } = ActionRunsOn.Web;
        public Guid? AgentId { get; set; }
        /// <summary>One per line: "Label", or "key | Label".</summary>
        public string? Parameters { get; set; }
        public string? Script { get; set; }

        public RequestActionInput ToInput() => new()
        {
            Name = Name ?? string.Empty,
            Description = Description,
            RunsOn = RunsOn,
            AgentId = AgentId,
            Script = Script ?? string.Empty,
            Parameters = (Parameters ?? string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(l =>
            {
                var bar = l.IndexOf('|');
                return bar < 0 ? new RequestActionParameterInput(null, l) : new RequestActionParameterInput(l[..bar].Trim(), l[(bar + 1)..].Trim());
            }).ToList()
        };

        public static ActionForm From(RequestAction a) => new()
        {
            Name = a.Name, Description = a.Description, RunsOn = a.RunsOn, AgentId = a.AgentId, Script = a.Script,
            Parameters = string.Join("\n", a.Parameters.OrderBy(p => p.DisplayOrder).Select(p => $"{p.Key} | {p.Label}"))
        };
    }

    public RequestAction? Item { get; private set; }
    [BindProperty] public ActionForm Form { get; set; } = new();
    public IReadOnlyList<ActionAgentChoice> Agents { get; private set; } = [];
    /// <summary>The web-side connection names a script may open, from configuration.</summary>
    public IReadOnlyList<string> WebConnections { get; private set; } = [];
    public string? SaveError { get; private set; }
    public IReadOnlyList<string> CheckResult { get; private set; } = [];
    public bool Checked { get; private set; }

    public async Task OnGetAsync(Guid? id, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        Form = Item is null ? new ActionForm { Script = Template } : ActionForm.From(Item);
    }

    public async Task<IActionResult> OnPostAsync(Guid? id, CancellationToken ct)
    {
        try
        {
            var saved = id is Guid existing ? await actions.UpdateAsync(existing, Form.ToInput(), ct) : await actions.CreateAsync(Form.ToInput(), ct);
            Success($"Action \"{saved.Name}\" saved.");
            return RedirectToPage(new { id = saved.Id });
        }
        catch (ValidationException ex) { SaveError = ex.Message; }
        await LoadAsync(id, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostCheckAsync(Guid? id, CancellationToken ct)
    {
        CheckResult = await actions.CheckAsync(Form.Script, ct);
        Checked = true;
        await LoadAsync(id, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var action = await actions.GetAsync(id, ct);
            await actions.DeleteAsync(id, ct);
            Success($"Action \"{action.Name}\" deleted.");
            return RedirectToPage("/Actions/Index");
        }
        catch (ValidationException ex)
        {
            Error(ex.Message);
            return RedirectToPage(new { id });
        }
    }

    private async Task LoadAsync(Guid? id, CancellationToken ct)
    {
        Item = id is Guid existing ? await actions.GetAsync(existing, ct) : null;
        Agents = await actions.AgentsAsync(ct);
        WebConnections = [.. actionOptions.Value.Connections.Keys.Order(), ActionOptions.OrbitConnection];
    }

    private const string Template = """
        // The request's values are in scope: Request.Number, Request.Requester.Name, Values["step.field"], Inputs["parameter"].
        // Log(...) writes to the step's output; the return value is appended to it. Connections.Open("name") opens a configured database.
        Log($"Running for {Request.Number} ({Request.Requester.Name})");
        return "done";
        """;
}
