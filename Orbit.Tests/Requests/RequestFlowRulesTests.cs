using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Requests;
using Orbit.Data.Entities;

namespace Orbit.Tests.Requests;

/// <summary>Request flows (spec §6.20): keys, what makes a step ready, dependencies, fields and when a flow is offered.</summary>
public class RequestFlowRulesTests
{
    private static RequestFlowStep Step(RequestStepKind kind, string key, int order = 0) =>
        new() { Key = key, Title = key, Kind = kind, DisplayOrder = order };

    private static RequestFlowStepDependency Dep(RequestFlowStep step, RequestFlowStep on, RequestOutcome? outcome = null) =>
        new() { StepId = step.Id, Step = step, DependsOnStepId = on.Id, DependsOnStep = on, RequiredOutcome = outcome };

    private static RequestFlow Flow(params RequestFlowStep[] steps)
    {
        var department = new Department { Name = "IT" };
        var category = new RequestCategory { Title = "Products", Department = department, DepartmentId = department.Id };
        var flow = new RequestFlow { Title = "Product code", Category = category, CategoryId = category.Id };
        foreach (var s in steps) { s.Flow = flow; s.FlowId = flow.Id; flow.Steps.Add(s); }
        return flow;
    }

    /// <summary>REQ-009: a key is lower-case letters, digits and hyphens, made from the title when blank; "request" is reserved; keys are unique.</summary>
    [Fact]
    public void Keys_are_slugs_made_from_titles()
    {
        Assert.Equal("product-details", RequestFlowRules.CleanKey(null, "Product details"));
        Assert.Equal("who-is-it-for", RequestFlowRules.CleanKey("", " Who is it for? "));
        Assert.Equal("my-key", RequestFlowRules.CleanKey("My_Key!", "ignored"));
        Assert.Equal(40, RequestFlowRules.CleanKey(new string('a', 50), null).Length);
        Assert.Throws<ValidationException>(() => RequestFlowRules.CleanKey("???", "!!!"));
        Assert.Throws<ValidationException>(() => RequestFlowRules.CleanKey("request", null));
        Assert.Throws<ValidationException>(() => RequestFlowRules.RequireUniqueKey(["details", "approval"], "details", "Another step"));
        RequestFlowRules.RequireUniqueKey(["details"], "details-2", "Another step");
    }

    /// <summary>REQ-010: what stops each kind of step from being used, and which kinds a person performs.</summary>
    [Fact]
    public void A_step_is_ready_once_its_kind_has_what_it_needs()
    {
        var form = Step(RequestStepKind.Form, "form");
        Assert.Equal(["has no fields"], RequestFlowRules.Problems(form));
        form.Fields.Add(new RequestFormField { Key = "q", Prompt = "Q" });
        Assert.Empty(RequestFlowRules.Problems(form));

        var approval = Step(RequestStepKind.Approval, "approval");
        Assert.Equal(["has no approval stage"], RequestFlowRules.Problems(approval));
        var stage = new RequestFlowApprovalStage { StageOrder = 1 };
        approval.Stages.Add(stage);
        Assert.Equal(["stage 1 has no approver"], RequestFlowRules.Problems(approval));
        stage.Approvers.Add(new RequestFlowApprover { Kind = ApproverKind.Person, UserId = Guid.NewGuid() });
        Assert.Empty(RequestFlowRules.Problems(approval));

        var task = Step(RequestStepKind.Task, "task");
        Assert.Equal(["has no department", "has no task title"], RequestFlowRules.Problems(task));
        task.TaskDepartmentId = Guid.NewGuid();
        task.TitleTemplate = "Code for {{form.q}}";
        Assert.Empty(RequestFlowRules.Problems(task));

        var action = Step(RequestStepKind.Action, "action");
        Assert.Equal(["has no action"], RequestFlowRules.Problems(action));
        var library = new RequestAction { Name = "Insert" };
        library.Parameters.Add(new RequestActionParameter { Key = "code", Label = "Code" });
        action.ActionId = library.Id;
        action.Action = library;
        Assert.Equal(["doesn't set the action's \"Code\""], RequestFlowRules.Problems(action));
        action.ActionInputs.Add(new RequestFlowStepActionInput { ParameterKey = "code", ValueTemplate = "{{form.q}}" });
        Assert.Empty(RequestFlowRules.Problems(action));

        var url = Step(RequestStepKind.Url, "page");
        Assert.Equal(["has no web address"], RequestFlowRules.Problems(url));
        url.Url = "https://example.com/{{form.q}}";
        Assert.Empty(RequestFlowRules.Problems(url));

        Assert.True(RequestFlowRules.IsPerformedByPerson(RequestStepKind.Form));
        Assert.True(RequestFlowRules.IsPerformedByPerson(RequestStepKind.Url));
        Assert.False(RequestFlowRules.IsPerformedByPerson(RequestStepKind.Task));

        // A web address is a full http(s) address; tokens are allowed in it and checked as "x".
        Assert.Equal("https://erp.example.com/products/{{form.q}}", RequestFlowRules.CleanUrl(" https://erp.example.com/products/{{form.q}} "));
        Assert.Throws<ValidationException>(() => RequestFlowRules.CleanUrl("/products"));
        Assert.Throws<ValidationException>(() => RequestFlowRules.CleanUrl("javascript:alert(1)"));
        Assert.Throws<ValidationException>(() => RequestFlowRules.CleanUrl(""));

        // Approvers name what their kind needs; parameters get keys from labels, all different.
        Assert.Throws<ValidationException>(() => RequestFlowRules.ValidateApprover(new RequestApproverInput { Kind = ApproverKind.Person }));
        Assert.Throws<ValidationException>(() => RequestFlowRules.ValidateApprover(new RequestApproverInput { Kind = ApproverKind.RoleInDepartment, RoleId = Guid.NewGuid() }));
        RequestFlowRules.ValidateApprover(new RequestApproverInput { Kind = ApproverKind.RoleInRequestersDepartment, RoleId = Guid.NewGuid() });
        RequestFlowRules.ValidateApprover(new RequestApproverInput { Kind = ApproverKind.RequestersManager });
        RequestFlowRules.ValidateApprover(new RequestApproverInput { Kind = ApproverKind.RequestersDepartmentManager });
        Assert.Throws<ValidationException>(() => RequestFlowRules.ValidateApprover(new RequestApproverInput { Kind = (ApproverKind)99 }));
        var parameters = RequestFlowRules.ValidateParameters([new(null, "Product code"), new("why", "Reason")]);
        Assert.Equal([("product-code", "Product code"), ("why", "Reason")], parameters);
        Assert.Throws<ValidationException>(() => RequestFlowRules.ValidateParameters([new("a", "A"), new("a", "B")]));
        Assert.Throws<ValidationException>(() => RequestFlowRules.ValidateParameters([new(null, " ")]));
    }

    /// <summary>REQ-011: dependencies - on another step of the flow, never twice, an outcome only on an approval, no circle; and the transitive predecessors.</summary>
    [Fact]
    public void Dependencies_form_a_directed_acyclic_graph()
    {
        var a = Step(RequestStepKind.Form, "a", 1);
        var b = Step(RequestStepKind.Approval, "b", 2);
        var c = Step(RequestStepKind.Task, "c", 3);
        var d = Step(RequestStepKind.Action, "d", 4);
        var steps = new[] { a, b, c, d };

        RequestFlowRules.ValidateDependencies(steps, [Dep(b, a), Dep(c, b, RequestOutcome.Accepted), Dep(d, c), Dep(d, a)]);
        Assert.Throws<ValidationException>(() => RequestFlowRules.ValidateDependencies(steps, [Dep(a, a)]));
        Assert.Throws<ValidationException>(() => RequestFlowRules.ValidateDependencies(steps, [Dep(b, a), Dep(b, a)]));
        Assert.Throws<ValidationException>(() => RequestFlowRules.ValidateDependencies(steps, [Dep(b, a, RequestOutcome.Declined)])); // a is a form
        Assert.Throws<ValidationException>(() => RequestFlowRules.ValidateDependencies(steps, [Dep(b, Step(RequestStepKind.Form, "elsewhere"))]));
        var circle = Assert.Throws<ValidationException>(() => RequestFlowRules.ValidateDependencies(steps, [Dep(b, a), Dep(c, b), Dep(a, c)]));
        Assert.Contains("circle", circle.Message);

        var deps = new[] { Dep(b, a), Dep(c, b), Dep(d, c), Dep(d, a) };
        Assert.Equal(new HashSet<Guid> { a.Id, b.Id, c.Id }, RequestFlowRules.TransitivePredecessors(d.Id, deps));
        Assert.Equal(new HashSet<Guid> { a.Id }, RequestFlowRules.TransitivePredecessors(b.Id, deps));
        Assert.Empty(RequestFlowRules.TransitivePredecessors(a.Id, deps));
    }

    /// <summary>
    /// REQ-013: a field's definition - prompt, choices only for a Choice (two to 50, distinct), a scope only for the pickers and asset
    /// types, the requester's department's for any of them, Held only for assets; an urgency has neither.
    /// </summary>
    [Fact]
    public void Fields_are_checked_by_type()
    {
        var text = RequestFlowRules.ValidateField(new RequestFieldInput { Prompt = " What is it? ", FieldType = RequestFieldType.Text, IsRequired = true, Choices = ["ignored"] });
        Assert.Equal(("what-is-it", "What is it?", true), (text.Key, text.Prompt, text.IsRequired));
        Assert.Empty(text.Choices);
        Assert.Null(text.Scope);

        Assert.Throws<ValidationException>(() => RequestFlowRules.ValidateField(new RequestFieldInput { Prompt = "" }));
        Assert.Throws<ValidationException>(() => RequestFlowRules.ValidateField(new RequestFieldInput { Prompt = "Pick", FieldType = RequestFieldType.Choice, Choices = ["only one"] }));
        Assert.Throws<ValidationException>(() => RequestFlowRules.ValidateField(new RequestFieldInput { Prompt = "Pick", FieldType = RequestFieldType.Choice, Choices = ["a", "A"] }));
        var choice = RequestFlowRules.ValidateField(new RequestFieldInput { Prompt = "Pick", FieldType = RequestFieldType.Choice, Choices = [" Red ", "", "Blue"] });
        Assert.Equal(["Red", "Blue"], choice.Choices);

        Assert.Throws<ValidationException>(() => RequestFlowRules.ValidateField(new RequestFieldInput { Prompt = "Which asset?", FieldType = RequestFieldType.Asset }));
        Assert.Throws<ValidationException>(() => RequestFlowRules.ValidateField(new RequestFieldInput { Prompt = "Who?", FieldType = RequestFieldType.User, PickerScope = RequestPickerScope.Held }));
        var asset = RequestFlowRules.ValidateField(new RequestFieldInput { Prompt = "Which asset?", FieldType = RequestFieldType.Asset, PickerScope = RequestPickerScope.Held }, "asset");
        Assert.Equal(("asset", RequestPickerScope.Held), (asset.Key, asset.Scope));
        var number = RequestFlowRules.ValidateField(new RequestFieldInput { Prompt = "How many?", FieldType = RequestFieldType.Number, PickerScope = RequestPickerScope.Company });
        Assert.Null(number.Scope);

        var urgency = RequestFlowRules.ValidateField(new RequestFieldInput { Prompt = "How urgent?", FieldType = RequestFieldType.Urgency, Choices = ["x", "y"], PickerScope = RequestPickerScope.Company });
        Assert.Empty(urgency.Choices);
        Assert.Null(urgency.Scope);
        Assert.Throws<ValidationException>(() => RequestFlowRules.ValidateField(new RequestFieldInput { Prompt = "What kind?", FieldType = RequestFieldType.AssetType }));
        Assert.Throws<ValidationException>(() => RequestFlowRules.ValidateField(new RequestFieldInput { Prompt = "What kind?", FieldType = RequestFieldType.AssetType, PickerScope = RequestPickerScope.Held }));
        var assetType = RequestFlowRules.ValidateField(new RequestFieldInput { Prompt = "What kind?", FieldType = RequestFieldType.AssetType, PickerScope = RequestPickerScope.Department });
        Assert.Equal(RequestPickerScope.Department, assetType.Scope);

        foreach (var type in new[] { RequestFieldType.Asset, RequestFieldType.AssetType, RequestFieldType.Project, RequestFieldType.User })
        {
            var own = RequestFlowRules.ValidateField(new RequestFieldInput { Prompt = "Which of yours?", FieldType = type, PickerScope = RequestPickerScope.RequestersDepartment });
            Assert.Equal(RequestPickerScope.RequestersDepartment, own.Scope);
        }
        var date = RequestFlowRules.ValidateField(new RequestFieldInput { Prompt = "When?", FieldType = RequestFieldType.Date, PickerScope = RequestPickerScope.RequestersDepartment });
        Assert.Null(date.Scope);
    }

    /// <summary>REQ-019: the start form is the first form with nothing to wait for, performed by the requester; a flow is live when offered and every step is ready.</summary>
    [Fact]
    public void The_start_form_and_the_live_rule()
    {
        var details = Step(RequestStepKind.Form, "details", 2);
        details.Fields.Add(new RequestFormField { Key = "q", Prompt = "Q" });
        var task = Step(RequestStepKind.Task, "task", 1);
        task.TaskDepartmentId = Guid.NewGuid();
        task.TitleTemplate = "T";
        var flow = Flow(task, details);
        Assert.Same(details, RequestFlowRules.StartForm(flow));
        Assert.True(RequestFlowRules.IsLive(flow));

        // A form addressed to a named person, or one that waits for a step, isn't the start form.
        details.PerformedById = Guid.NewGuid();
        Assert.Null(RequestFlowRules.StartForm(flow));
        details.PerformedById = null;
        details.Dependencies.Add(Dep(details, task));
        Assert.Null(RequestFlowRules.StartForm(flow));

        task.TitleTemplate = null;
        Assert.False(RequestFlowRules.IsLive(flow));
        task.TitleTemplate = "T";
        flow.IsArchived = true;
        Assert.False(RequestFlowRules.IsLive(flow));
        flow.IsArchived = false;
        flow.Category.Department.IsArchived = true;
        Assert.False(RequestFlowRules.IsLive(flow));
        Assert.False(RequestFlowRules.IsLive(Flow()));
    }
}
