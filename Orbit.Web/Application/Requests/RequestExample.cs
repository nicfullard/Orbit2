using Orbit.Data.Entities;

namespace Orbit.Application.Requests;

public sealed record ExampleCategory(string Title, string Description, string Icon, RequestColour Colour, IReadOnlyList<ExampleOption> Options);

public sealed record ExampleOption(
    string Title, string Description, RequestOptionKind Kind, IReadOnlyList<ExampleQuestion> Questions, string? Url = null, TaskType TaskType = TaskType.Task);

public sealed record ExampleQuestion(
    string Prompt, RequestQuestionType Type, bool IsRequired, string? HelpText = null, IReadOnlyList<string>? Choices = null, bool SetsDueDate = false);

/// <summary>
/// The starter catalogue a department with no request categories can load (§6.20): generic enough for any department - nothing in it
/// is about IT - and using every question type and both kinds of option, so it doubles as a worked example. The Change something
/// flows file Change tasks, the Request something new flows Request tasks and the rest plain Tasks; the link points at example.com
/// until someone replaces it.
/// </summary>
public static class RequestExample
{
    public const string PlaceholderUrl = "https://example.com/";

    private static ExampleQuestion Urgency(bool required = true) =>
        new("How urgent is it?", RequestQuestionType.Urgency, required);

    private static ExampleQuestion NeededBy(string prompt = "When do you need it by?") =>
        new(prompt, RequestQuestionType.Date, false, "Leave it blank if there's no particular date.", SetsDueDate: true);

    public static readonly IReadOnlyList<ExampleCategory> Catalogue =
    [
        new("Report a problem", "Something is broken, not working or has gone wrong.", "warning", RequestColour.Red,
        [
            new("Something isn't working", "Equipment, a system or a service has stopped working or is misbehaving.", RequestOptionKind.Flow,
            [
                new("What's the problem?", RequestQuestionType.Text, true, "Describe what happened, and any message you saw."),
                new("Which asset is affected?", RequestQuestionType.Asset, false, "Pick it from the assets assigned to you, or describe it if it isn't one of yours."),
                new("When did it start?", RequestQuestionType.Date, false),
                Urgency()
            ]),
            new("Something is incorrect", "Information, a document or a record is wrong.", RequestOptionKind.Flow,
            [
                new("What is incorrect?", RequestQuestionType.Text, true, "Say where you saw it, so it can be found."),
                new("What should it be?", RequestQuestionType.Text, false),
                Urgency()
            ])
        ]),
        new("Change something", "Change something that already exists.", "pencil", RequestColour.Orange,
        [
            new("Change something that exists", "A process, document, setting or record that needs to be different.", RequestOptionKind.Flow,
            [
                new("What needs to change?", RequestQuestionType.Text, true),
                new("Why is the change needed?", RequestQuestionType.Text, true),
                new("Who does it affect?", RequestQuestionType.Choice, true, Choices: ["Just me", "My team", "My department", "Everyone"]),
                NeededBy()
            ], TaskType: TaskType.Change),
            new("Access change", "Give, change or remove someone's access to a system, room or resource.", RequestOptionKind.Flow,
            [
                new("Who is the change for?", RequestQuestionType.User, true),
                new("Give, change or remove access?", RequestQuestionType.Choice, true, Choices: ["Give access", "Change access", "Remove access"]),
                new("Access to what?", RequestQuestionType.Text, true),
                NeededBy("From when?")
            ], TaskType: TaskType.Change)
        ]),
        new("Request something new", "Ask for something that doesn't exist yet.", "plus", RequestColour.Green,
        [
            new("Equipment or supplies", "Order equipment, furniture, tools or supplies.", RequestOptionKind.Flow,
            [
                new("What do you need?", RequestQuestionType.Text, true, "Include a make, model or link if you have one in mind."),
                new("How many?", RequestQuestionType.Number, true),
                new("What is it for?", RequestQuestionType.Text, false),
                NeededBy()
            ], TaskType: TaskType.Request),
            new("A new service or improvement", "Suggest something new, or a better way of doing something.", RequestOptionKind.Flow,
            [
                new("What would you like?", RequestQuestionType.Text, true),
                new("What would it improve?", RequestQuestionType.Text, true),
                NeededBy()
            ], TaskType: TaskType.Request)
        ]),
        new("Help and self-service", "Guides, answers and things you can do yourself.", "book", RequestColour.Purple,
        [
            new("Guides and how-tos", "Step-by-step guides. (An example link: replace it with your own.)", RequestOptionKind.Link, [], PlaceholderUrl),
            new("Ask a question", "Not sure where to start? Ask, and someone will get back to you.", RequestOptionKind.Flow,
            [
                new("What's your question?", RequestQuestionType.Text, true),
                Urgency(required: false)
            ])
        ])
    ];
}
