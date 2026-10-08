using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Tests.Users;

/// <summary>Who may be a person's or a department's manager (spec §6.5, §6.6; MGR-001 and MGR-002).</summary>
public class ManagerRulesTests
{
    /// <summary>MGR-001: a manager is an active person, never a system account.</summary>
    [Fact]
    public void A_manager_is_an_active_person()
    {
        Assert.Null(ManagerRules.Refusal(new ApplicationUser { DisplayName = "Ann" }));
        Assert.Contains("Bob", ManagerRules.Refusal(new ApplicationUser { DisplayName = "Bob", IsActive = false }));
        Assert.NotNull(ManagerRules.Refusal(new ApplicationUser { DisplayName = "Claude", IsSystemAccount = true }));
    }

    /// <summary>MGR-002: nobody reports to themselves - not directly, and not through the people who report to them.</summary>
    [Fact]
    public void Nobody_reports_to_themselves()
    {
        var ann = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var cat = Guid.NewGuid();
        var dan = Guid.NewGuid();
        // Cat reports to Bob, Bob to Ann; Dan reports to nobody.
        var lines = new Dictionary<Guid, Guid> { [cat] = bob, [bob] = ann };

        Assert.True(ManagerRules.ReportsInACircle(ann, ann, lines));
        Assert.True(ManagerRules.ReportsInACircle(ann, bob, lines)); // Bob reports to Ann
        Assert.True(ManagerRules.ReportsInACircle(ann, cat, lines)); // ... and so does Cat, through Bob

        Assert.False(ManagerRules.ReportsInACircle(ann, dan, lines));
        Assert.False(ManagerRules.ReportsInACircle(cat, ann, lines)); // up the line she is already on
        Assert.False(ManagerRules.ReportsInACircle(dan, cat, lines));
        Assert.False(ManagerRules.ReportsInACircle(cat, dan, lines)); // moving to another manager
    }

    /// <summary>MGR-002: a circle already in the data that doesn't pass through the person neither loops forever nor refuses their change.</summary>
    [Fact]
    public void An_old_circle_elsewhere_is_not_this_changes_doing()
    {
        var ann = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var cat = Guid.NewGuid();
        var lines = new Dictionary<Guid, Guid> { [bob] = cat, [cat] = bob };

        Assert.False(ManagerRules.ReportsInACircle(ann, bob, lines));
    }
}
