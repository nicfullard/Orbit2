using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Requests;
using Orbit.Data.Entities;
using Orbit.Helpers;

namespace Orbit.Tests.Requests;

/// <summary>Request flows (spec §6.20): what an option and its questions may hold, when one is offered, answers, and the task a request becomes.</summary>
public class RequestRulesTests
{
    private static readonly DateOnly Today = new(2026, 9, 28);

    private static RequestQuestion Q(RequestQuestionType type, string prompt = "Question", bool required = false, int order = 1,
        bool setsDueDate = false, params string[] choices) =>
        new() { Prompt = prompt, QuestionType = type, IsRequired = required, DisplayOrder = order, SetsDueDate = setsDueDate, Choices = choices.ToList() };

    private static RequestOption Flow(params RequestQuestion[] questions)
    {
        var department = new Department { Name = "Information Systems" };
        var category = new RequestCategory { Title = "Report a problem", Department = department, DepartmentId = department.Id };
        var option = new RequestOption { Title = "Something isn't working", Category = category, CategoryId = category.Id, Kind = RequestOptionKind.Flow };
        foreach (var q in questions) option.Questions.Add(q);
        category.Options.Add(option);
        return option;
    }

    private static Dictionary<Guid, RequestAnswerInput> Answers(params (RequestQuestion Q, string? Value, Guid? AssetId)[] answers) =>
        answers.ToDictionary(a => a.Q.Id, a => new RequestAnswerInput(a.Value, a.AssetId));

    /// <summary>REQ-001: a link must be a full http(s) address, since it opens in a new tab.</summary>
    [Fact]
    public void Link_addresses_must_be_full_web_addresses()
    {
        Assert.Equal("https://example.com/help", RequestRules.CleanUrl("  https://example.com/help "));
        Assert.Equal("http://intranet.local/wiki", RequestRules.CleanUrl("http://intranet.local/wiki"));
        foreach (var bad in new[] { "", "   ", "example.com", "/help", "javascript:alert(1)", "file:///c:/x", "data:text/html,hi", "ftp://x.org", "https://" })
            Assert.Throws<ValidationException>(() => RequestRules.CleanUrl(bad));
        Assert.Throws<ValidationException>(() => RequestRules.CleanUrl("https://example.com/" + new string('a', RequestRules.MaxUrlLength)));

        // A flow has no address, whatever was typed; a link needs one.
        Assert.Null(RequestRules.ValidateOption(new RequestOptionInput { Title = "Flow", Kind = RequestOptionKind.Flow, Url = "javascript:x" }).Url);
        Assert.Throws<ValidationException>(() => RequestRules.ValidateOption(new RequestOptionInput { Title = "Link", Kind = RequestOptionKind.Link }));
        Assert.Throws<ValidationException>(() => RequestRules.ValidateOption(new RequestOptionInput { Title = " ", Kind = RequestOptionKind.Flow }));
        Assert.Throws<ValidationException>(() => RequestRules.ValidateOption(new RequestOptionInput { Title = "x", TaskType = (TaskType)99 }));
    }

    /// <summary>REQ-002: a question's definition, and a flow's questions together - each task field filled by one question at most.</summary>
    [Fact]
    public void Questions_and_flows_are_checked()
    {
        var choice = RequestRules.ValidateQuestion(new RequestQuestionInput
        {
            Prompt = " Who does it affect? ", QuestionType = RequestQuestionType.Choice, Choices = ["Just me", " ", "My team ", "Everyone"]
        });
        Assert.Equal("Who does it affect?", choice.Prompt);
        Assert.Equal(new[] { "Just me", "My team", "Everyone" }, choice.Choices);
        Assert.Empty(RequestRules.ValidateQuestion(new RequestQuestionInput { Prompt = "What?", Choices = ["a", "b"] }).Choices);

        Assert.Throws<ValidationException>(() => RequestRules.ValidateQuestion(new RequestQuestionInput { Prompt = "  " }));
        Assert.Throws<ValidationException>(() => RequestRules.ValidateQuestion(new RequestQuestionInput
            { Prompt = "Pick", QuestionType = RequestQuestionType.Choice, Choices = ["Only one"] }));
        Assert.Throws<ValidationException>(() => RequestRules.ValidateQuestion(new RequestQuestionInput
            { Prompt = "Pick", QuestionType = RequestQuestionType.Choice, Choices = ["Yes", "YES"] }));
        Assert.Throws<ValidationException>(() => RequestRules.ValidateQuestion(new RequestQuestionInput
            { Prompt = "Pick", QuestionType = RequestQuestionType.Choice, Choices = Enumerable.Range(1, RequestRules.MaxChoices + 1).Select(i => $"c{i}").ToList() }));
        Assert.Throws<ValidationException>(() => RequestRules.ValidateQuestion(new RequestQuestionInput
            { Prompt = "When?", QuestionType = RequestQuestionType.Text, SetsDueDate = true }));
        Assert.True(RequestRules.ValidateQuestion(new RequestQuestionInput { Prompt = "By when?", QuestionType = RequestQuestionType.Date, SetsDueDate = true }).SetsDueDate);

        RequestRules.CheckFlow([Q(RequestQuestionType.Urgency), Q(RequestQuestionType.Asset), Q(RequestQuestionType.Date, setsDueDate: true), Q(RequestQuestionType.Date)]);
        Assert.Throws<ValidationException>(() => RequestRules.CheckFlow([Q(RequestQuestionType.Urgency), Q(RequestQuestionType.Urgency)]));
        Assert.Throws<ValidationException>(() => RequestRules.CheckFlow([Q(RequestQuestionType.Asset), Q(RequestQuestionType.Asset)]));
        Assert.Throws<ValidationException>(() => RequestRules.CheckFlow([Q(RequestQuestionType.Date, setsDueDate: true), Q(RequestQuestionType.Date, setsDueDate: true)]));
        Assert.Throws<ValidationException>(() => RequestRules.CheckFlow(Enumerable.Range(0, RequestRules.MaxQuestions + 1).Select(_ => Q(RequestQuestionType.Text)).ToList()));

        Assert.Equal(RequestIcons.Default, RequestRules.CleanIcon(null));
        Assert.Equal("tools", RequestRules.CleanIcon("tools"));
        Assert.Throws<ValidationException>(() => RequestRules.CleanIcon("arrow-right")); // drawn by the pages, not offered for a category
        Assert.Throws<ValidationException>(() => RequestRules.CleanColour((RequestColour)42));
    }

    /// <summary>REQ-003: each answer in its canonical form; refused and missing required answers are reported per question.</summary>
    [Fact]
    public void Answers_are_cleaned_and_checked_per_question()
    {
        var text = Q(RequestQuestionType.Text, "What's the problem?", required: true, order: 1);
        var count = Q(RequestQuestionType.Number, "How many?", order: 2);
        var started = Q(RequestQuestionType.Date, "When did it start?", order: 3);
        var needed = Q(RequestQuestionType.Date, "Needed by?", order: 4, setsDueDate: true);
        var urgency = Q(RequestQuestionType.Urgency, "How urgent?", order: 5);
        var who = Q(RequestQuestionType.Choice, "Who?", order: 6, choices: ["Just me", "Everyone"]);
        var asset = Q(RequestQuestionType.Asset, "Which asset?", required: true, order: 7);
        var questions = new[] { text, count, started, needed, urgency, who, asset };
        var assetId = Guid.NewGuid();

        var (answers, errors) = RequestRules.CleanAnswers(questions, Answers(
            (text, "  The printer jams\r\nevery time  ", null), (count, "2.50", null), (started, "2026-09-01", null), (needed, "2026-09-28", null),
            (urgency, "high", null), (who, "everyone", null), (asset, null, assetId)), Today);
        Assert.Empty(errors);
        Assert.Equal(7, answers.Count);
        Assert.Equal("The printer jams\nevery time", answers[0].Value);
        Assert.Equal("2.5", answers[1].Value);
        Assert.Equal("2026-09-01", answers[2].Value); // a date that doesn't set the due date may be in the past
        Assert.Equal("High", answers[4].Value);
        Assert.Equal("Everyone", answers[5].Value);
        Assert.Equal(assetId, answers[6].AssetId);
        Assert.Null(answers[6].Value);

        (answers, errors) = RequestRules.CleanAnswers(questions, Answers(
            (text, "   ", null), (count, "two", null), (started, "yesterday", null), (needed, "2026-09-27", null),
            (urgency, "2", null), (who, "Nobody", null), (asset, "", Guid.Empty)), Today);
        Assert.Empty(answers);
        Assert.Equal(questions.Select(q => q.Id).OrderBy(i => i), errors.Keys.OrderBy(i => i));
        Assert.Contains("past", errors[needed.Id]);

        // Optional questions may be left out; an asset question is answered by a description alone; a missing entry is no answer.
        (answers, errors) = RequestRules.CleanAnswers(questions, Answers((text, "Help", null), (asset, "The big printer on floor 2", null)), Today);
        Assert.Empty(errors);
        Assert.Equal(2, answers.Count);
        Assert.Null(answers[1].AssetId);
        Assert.Throws<ValidationException>(() => RequestRules.CleanAnswer(text, new RequestAnswerInput(new string('x', RequestRules.MaxTextAnswerLength + 1)), Today));
    }

    /// <summary>REQ-004: the task a request becomes - title, description, priority, due date and asset.</summary>
    [Fact]
    public void A_request_is_composed_into_a_task()
    {
        var text = Q(RequestQuestionType.Text, "What's the problem?", order: 1);
        var more = Q(RequestQuestionType.Text, "Anything else?", order: 2);
        var needed = Q(RequestQuestionType.Date, "Needed by?", order: 3, setsDueDate: true);
        var urgency = Q(RequestQuestionType.Urgency, "How urgent is it?", order: 4);
        var asset = Q(RequestQuestionType.Asset, "Which asset?", order: 5);
        var option = Flow(text, more, needed, urgency, asset);
        var assetId = Guid.NewGuid();
        var answers = new List<RequestAnswer>
        {
            new(text, "The   printer jams\nwhen printing double-sided"),
            new(more, "It started after the update"),
            new(needed, "2026-10-02"),
            new(urgency, "Critical"),
            new(asset, "Second floor", assetId)
        };

        var composed = RequestRules.Compose(option, answers, "Jane Smith", "Finance", "PR-0042 - LaserJet");
        Assert.Equal("Something isn't working: The printer jams", composed.Title);
        Assert.Equal(TaskPriority.Critical, composed.Priority);
        Assert.Equal(new DateOnly(2026, 10, 2), composed.DueDate);
        Assert.Equal(assetId, composed.AssetId);
        Assert.Equal(
            "Logged through Requests by Jane Smith (Finance).\n" +
            "Request: Report a problem \u203a Something isn't working\n" +
            "\nWhat's the problem?\nThe   printer jams\nwhen printing double-sided\n" +
            "\nAnything else?\nIt started after the update\n" +
            "\nNeeded by?\n2026-10-02\n" +
            "\nHow urgent is it?\nCritical - Work has stopped for me or for several people\n" +
            "\nWhich asset?\nPR-0042 - LaserJet (linked to this task)\nSecond floor",
            composed.Description);

        // Nothing but an unlisted asset: the title names the requester, priority is Medium, no due date or linked asset.
        var bare = RequestRules.Compose(option, [new RequestAnswer(asset, "The big printer")], "Jane Smith", null);
        Assert.Equal("Something isn't working for Jane Smith", bare.Title);
        Assert.Equal(TaskPriority.Medium, bare.Priority);
        Assert.Null(bare.DueDate);
        Assert.Null(bare.AssetId);
        Assert.StartsWith("Logged through Requests by Jane Smith.\n", bare.Description);

        // The title takes the first line that has text, its spaces collapsed; a long one is cut, and the whole title always fits a
        // task's 300 characters.
        Assert.Equal("a b", RequestRules.FirstLine("\n  \n a   b \nc", RequestRules.MaxTitleLineLength));
        var longLine = RequestRules.Compose(option, [new RequestAnswer(text, new string('a', 500))], "Jane", null);
        Assert.Equal(option.Title.Length + 2 + RequestRules.MaxTitleLineLength, longLine.Title.Length);
        Assert.EndsWith("...", longLine.Title);
        option.Title = new string('o', RequestRules.MaxOptionTitleLength);
        Assert.True(RequestRules.Compose(option, [], new string('n', 200), null).Title.Length <= 300);
    }

    /// <summary>REQ-008: a User question names one of the people offered; it becomes who the request is for, and titles it.</summary>
    [Fact]
    public void A_user_question_names_who_the_request_is_for()
    {
        var who = Q(RequestQuestionType.User, "Who is the change for?", required: true, order: 1);
        var what = Q(RequestQuestionType.Choice, "Give or remove?", order: 2, choices: ["Give access", "Remove access"]);
        var option = Flow(who, what);
        var bob = Guid.NewGuid();
        var people = new Dictionary<Guid, string> { [bob] = "Bob Smith", [Guid.NewGuid()] = "Jane Smith" };

        var (answers, errors) = RequestRules.CleanAnswers(option.Questions, Answers((who, bob.ToString(), null), (what, "give access", null)), Today, people);
        Assert.Empty(errors);
        Assert.Equal("Bob Smith", answers[0].Value);
        Assert.Equal(bob, answers[0].UserId);

        // Someone not offered, a made-up id, or no people at all is refused; left out of a required question, it is missing.
        foreach (var refused in new[] { Guid.NewGuid().ToString(), "Bob Smith", "" })
        {
            (_, errors) = RequestRules.CleanAnswers(option.Questions, Answers((who, refused, null)), Today, people);
            Assert.True(errors.ContainsKey(who.Id), refused);
        }
        (_, errors) = RequestRules.CleanAnswers(option.Questions, Answers((who, bob.ToString(), null)), Today);
        Assert.True(errors.ContainsKey(who.Id));

        var composed = RequestRules.Compose(option, answers, "Jane Smith", "Finance");
        Assert.Equal(bob, composed.RequestedForId);
        Assert.Equal("Something isn't working for Bob Smith", composed.Title);
        Assert.Contains("Who is the change for?\nBob Smith\n", composed.Description);
        Assert.Null(RequestRules.Compose(option, [answers[1]], "Jane Smith", null).RequestedForId);

        // One per flow: it fills one task field.
        Assert.Throws<ValidationException>(() => RequestRules.CheckFlow([Q(RequestQuestionType.User), Q(RequestQuestionType.User)]));
    }

    /// <summary>REQ-005: an option is offered while it, its category and its department are open and it can do something.</summary>
    [Fact]
    public void Only_live_options_are_offered()
    {
        var option = Flow(Q(RequestQuestionType.Text));
        Assert.True(RequestRules.IsLive(option));

        option.IsArchived = true;
        Assert.False(RequestRules.IsLive(option));
        option.IsArchived = false;
        option.Category.IsArchived = true;
        Assert.False(RequestRules.IsLive(option));
        option.Category.IsArchived = false;
        option.Category.Department.IsArchived = true;
        Assert.False(RequestRules.IsLive(option));
        option.Category.Department.IsArchived = false;

        Assert.False(RequestRules.IsLive(Flow())); // a flow with no questions
        var link = Flow();
        link.Kind = RequestOptionKind.Link;
        Assert.False(RequestRules.IsLive(link));
        link.Url = "https://example.com/";
        Assert.True(RequestRules.IsLive(link));
    }

    /// <summary>REQ-006: the example catalogue passes every check a configured one must, uses every question type and both kinds.</summary>
    [Fact]
    public void The_example_catalogue_is_valid()
    {
        var types = new HashSet<RequestQuestionType>();
        var kinds = new HashSet<RequestOptionKind>();
        foreach (var c in RequestExample.Catalogue)
        {
            RequestRules.RequireTitle(c.Title, RequestRules.MaxCategoryTitleLength);
            Assert.Equal(c.Icon, RequestRules.CleanIcon(c.Icon));
            Assert.Equal(c.Options.Count, c.Options.Select(o => o.Title.ToLowerInvariant()).Distinct().Count());
            foreach (var o in c.Options)
            {
                kinds.Add(o.Kind);
                RequestRules.ValidateOption(new RequestOptionInput { Title = o.Title, Description = o.Description, Kind = o.Kind, Url = o.Url, TaskType = o.TaskType });
                var questions = o.Questions.Select(q =>
                {
                    var d = RequestRules.ValidateQuestion(new RequestQuestionInput
                        { Prompt = q.Prompt, HelpText = q.HelpText, QuestionType = q.Type, IsRequired = q.IsRequired, Choices = q.Choices ?? [], SetsDueDate = q.SetsDueDate });
                    types.Add(d.Type);
                    return new RequestQuestion { QuestionType = d.Type, SetsDueDate = d.SetsDueDate };
                }).ToList();
                RequestRules.CheckFlow(questions);
                Assert.True(o.Kind == RequestOptionKind.Link || questions.Count > 0, o.Title);
            }
        }
        Assert.Equal(Enum.GetValues<RequestQuestionType>(), types.OrderBy(t => t));
        Assert.Equal(2, kinds.Count);
        Assert.Equal(RequestExample.Catalogue.Count, RequestExample.Catalogue.Select(c => c.Title.ToLowerInvariant()).Distinct().Count());
    }

    /// <summary>REQ-006: the example's Change something flows file Change tasks, its Request something new flows Request tasks, the rest Task.</summary>
    [Fact]
    public void The_example_catalogue_files_change_and_request_tasks()
    {
        var expected = new Dictionary<string, TaskType> { ["Change something"] = TaskType.Change, ["Request something new"] = TaskType.Request };
        foreach (var c in RequestExample.Catalogue)
            foreach (var o in c.Options.Where(o => o.Kind == RequestOptionKind.Flow))
                Assert.Equal(expected.GetValueOrDefault(c.Title, TaskType.Task), o.TaskType);
        Assert.All(expected.Keys, title => Assert.Contains(RequestExample.Catalogue, c => c.Title == title));
    }
}
