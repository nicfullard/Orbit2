using Orbit.Application;
using Orbit.Application.Requests;
using Orbit.Data.Entities;

namespace Orbit.Tests.Requests;

/// <summary>Tokens (spec §6.20): what a template may refer to, and how it is rendered.</summary>
public class RequestTokenRulesTests
{
    /// <summary>REQ-012: tokens are parsed from a template, each step offers its kind's values, a template may only use the request's and its predecessors', and rendering fills them in.</summary>
    [Fact]
    public void Tokens_are_parsed_offered_checked_and_rendered()
    {
        Assert.Equal(["details.description", "request.number"], RequestTokenRules.Parse("Code for {{ details.description }} ({{request.number}}) {{DETAILS.description}}"));
        Assert.Empty(RequestTokenRules.Parse(null));
        Assert.Empty(RequestTokenRules.Parse("no tokens {{ }} {{bad token}}"));

        var form = new RequestFlowStep { Key = "details", Title = "Details", Kind = RequestStepKind.Form, DisplayOrder = 1 };
        form.Fields.Add(new RequestFormField { Key = "description", Prompt = "Description", DisplayOrder = 2 });
        form.Fields.Add(new RequestFormField { Key = "segment", Prompt = "Segment", DisplayOrder = 1 });
        Assert.Equal(["details.segment", "details.description"], RequestTokenRules.Offered(form).Select(t => t.Name));
        var task = new RequestFlowStep { Key = "code", Title = "Code", Kind = RequestStepKind.Task, DisplayOrder = 2 };
        Assert.Equal(["code.number", "code.title", "code.assignees", "code.completed-by", "code.completed-on"], RequestTokenRules.Offered(task).Select(t => t.Name));
        var approval = new RequestFlowStep { Key = "sign-off", Kind = RequestStepKind.Approval, DisplayOrder = 3 };
        Assert.Equal(["sign-off.outcome", "sign-off.comments"], RequestTokenRules.Offered(approval).Select(t => t.Name));
        var action = new RequestFlowStep { Key = "insert", Kind = RequestStepKind.Action };
        Assert.Equal(["insert.output"], RequestTokenRules.Offered(action).Select(t => t.Name));
        Assert.Empty(RequestTokenRules.Offered(new RequestFlowStep { Key = "page", Kind = RequestStepKind.Url }));

        var available = RequestTokenRules.Available([task, form]);
        Assert.Equal("request.number", available[0].Name);
        Assert.Equal(RequestTokenRules.RequestTokens.Count + 2 + 5, available.Count);
        Assert.Equal("{{details.segment}}", available[RequestTokenRules.RequestTokens.Count].Text);
        var names = available.Select(t => t.Name).ToHashSet();

        RequestTokenRules.Validate("{{request.requester}} asks for {{details.description}} ({{code.number}})", names, "The title");
        RequestTokenRules.Validate(null, names, "The title");
        var refused = Assert.Throws<ValidationException>(() => RequestTokenRules.Validate("{{sign-off.outcome}}", names, "The title"));
        Assert.Contains("{{sign-off.outcome}}", refused.Message);
        Assert.Throws<ValidationException>(() => RequestTokenRules.Validate("{{details.nope}}", names, "The title"));

        var values = new Dictionary<string, string?> { ["details.description"] = "Blue widget", ["request.number"] = "R-26-00001", ["details.segment"] = null };
        Assert.Equal("R-26-00001: Blue widget []", RequestTokenRules.Render("{{request.number}}: {{ details.description }} [{{details.segment}}]", values));
        Assert.Equal("", RequestTokenRules.Render(null, values));
        Assert.Equal("x and x", RequestTokenRules.Render("{{a.b}} and {{c}}", _ => "x"));
        Assert.Equal("https://x/?p=Blue%20widget&r=R-26-00001", RequestTokenRules.RenderUrl("https://x/?p={{details.description}}&r={{request.number}}", values));
    }
}
