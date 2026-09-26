using Orbit.Application;
using Orbit.Application.Assets;
using Orbit.Application.Models;
using Orbit.Data.Entities;
using Orbit.Tests.Access;

namespace Orbit.Tests.Assets;

/// <summary>Asset numbers, disposal, checks and deleting (spec §6.19).</summary>
public class AssetRulesTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);

    /// <summary>AST-005: disposal needs a sensible date; a disposed asset can't be checked; reinstating clears the date.</summary>
    [Fact]
    public void Disposal_date_and_what_a_disposed_asset_can_do()
    {
        Assert.Equal(Today, AssetRules.DisposalDate(AssetStatus.Disposed, null, null, Today));
        Assert.Equal(new DateOnly(2026, 9, 1), AssetRules.DisposalDate(AssetStatus.Disposed, new DateOnly(2026, 9, 1), new DateOnly(2024, 2, 1), Today));
        Assert.Throws<ValidationException>(() => AssetRules.DisposalDate(AssetStatus.Disposed, Today.AddDays(1), null, Today));
        Assert.Throws<ValidationException>(() => AssetRules.DisposalDate(AssetStatus.Disposed, new DateOnly(2023, 1, 1), new DateOnly(2024, 2, 1), Today));
        Assert.Null(AssetRules.DisposalDate(AssetStatus.Active, new DateOnly(2026, 9, 1), null, Today));

        var holder = TestActors.Member(Guid.NewGuid());
        var disposed = new Asset { DepartmentId = Guid.NewGuid(), Status = AssetStatus.Disposed };
        disposed.Assignments.Add(new AssetAssignment { UserId = holder.UserId!.Value });
        Assert.False(AccessPolicy.CanCheckAsset(holder, disposed, isAssigned: true));
        Assert.False(AccessPolicy.CanCheckAsset(TestActors.SystemAdmin(), disposed, isAssigned: false));
        Assert.Throws<ValidationException>(() => AssetRules.ValidateCheck(AssetStatus.Disposed, AssetCheckOutcome.Ok, Today, null, Today));
        // A lost asset can be checked: finding it is a check.
        AssetRules.ValidateCheck(AssetStatus.Lost, AssetCheckOutcome.Ok, Today, null, Today);
    }

    /// <summary>
    /// AST-013 (the rule half): the asset number is the ERP asset register number, which not every asset has - blank is none;
    /// when given it is trimmed, at most 50, and compared ignoring case. Two assets without one never clash.
    /// </summary>
    [Fact]
    public void Asset_numbers_are_optional_trimmed_and_compared_ignoring_case()
    {
        Assert.Equal("FA-004211", AssetRules.NormaliseAssetNumber("  FA-004211 "));
        Assert.Null(AssetRules.NormaliseAssetNumber("  "));
        Assert.Null(AssetRules.NormaliseAssetNumber(null));
        Assert.Throws<ValidationException>(() => AssetRules.NormaliseAssetNumber(new string('9', 51)));
        Assert.True(AssetRules.SameAssetNumber("fa-004211", " FA-004211"));
        Assert.False(AssetRules.SameAssetNumber("FA-004211", "FA-004212"));
        Assert.False(AssetRules.SameAssetNumber(null, null));
        Assert.False(AssetRules.SameAssetNumber("FA-004211", null));
        Assert.Equal("FA-004211 - Reception laptop", AssetRules.Label("FA-004211", "Reception laptop"));
        Assert.Equal("Reception laptop", AssetRules.Label(null, "Reception laptop"));
    }

    /// <summary>AST-014 (the rule half): only an asset without checks can be deleted.</summary>
    [Fact]
    public void Only_an_asset_without_checks_can_be_deleted()
    {
        Assert.Null(AssetRules.DeleteBlocker(0));
        Assert.Contains("Dispose of it instead", AssetRules.DeleteBlocker(1));
        Assert.Contains("3 checks", AssetRules.DeleteBlocker(3));
    }

    /// <summary>AST-024 (the rule half): an asset with linked tasks, or a recurring task about it, can't be deleted either.</summary>
    [Fact]
    public void An_asset_with_linked_tasks_or_recurring_tasks_cannot_be_deleted()
    {
        Assert.Null(AssetRules.DeleteBlocker(0, 0, 0));
        Assert.Equal("This asset has a linked task on record, so it can't be deleted. Dispose of it instead.", AssetRules.DeleteBlocker(0, 1));
        Assert.Contains("2 linked tasks", AssetRules.DeleteBlocker(0, 2));
        Assert.Contains("a recurring task", AssetRules.DeleteBlocker(0, 0, 1));
        Assert.Contains("a check, 4 linked tasks and 2 recurring tasks", AssetRules.DeleteBlocker(1, 4, 2));
    }

    /// <summary>
    /// AST-023 (the rule half): a task can be linked to an asset the caller can see that isn't disposed. Damaged and lost assets are
    /// exactly the ones tasks are raised about, so only disposal refuses.
    /// </summary>
    [Fact]
    public void A_task_links_to_a_visible_asset_that_is_not_disposed()
    {
        foreach (var status in new[] { AssetStatus.Active, AssetStatus.InStorage, AssetStatus.Damaged, AssetStatus.Lost })
            AssetRules.RequireLinkable(new Asset { Name = "Van", Status = status }, canView: true);

        var disposed = Assert.Throws<ValidationException>(() =>
            AssetRules.RequireLinkable(new Asset { AssetNumber = "FA-1", Name = "Old van", Status = AssetStatus.Disposed }, canView: true));
        Assert.Equal("\"FA-1 - Old van\" is disposed, so a task can't be linked to it.", disposed.Message);
        var hidden = Assert.Throws<ValidationException>(() => AssetRules.RequireLinkable(new Asset { Name = "Van" }, canView: false));
        Assert.Contains("permission to see that asset", hidden.Message);
    }

    /// <summary>AST-025: a scan or typed text picks an asset outright when it is exactly its ERP number or serial number, ignoring case and spaces.</summary>
    [Fact]
    public void A_scan_matches_the_asset_number_or_serial_exactly()
    {
        Assert.True(AssetRules.MatchesScan("FA-004211", "SN123", " fa-004211 "));
        Assert.True(AssetRules.MatchesScan(null, " SN123 ", "sn123"));
        Assert.False(AssetRules.MatchesScan("FA-004211", "SN123", "SN12"));
        Assert.False(AssetRules.MatchesScan("FA-004211", null, "004211"));
        Assert.False(AssetRules.MatchesScan(null, null, "anything"));
        Assert.False(AssetRules.MatchesScan("FA-004211", "SN123", "  "));
    }

    /// <summary>AST-016: an issue found needs notes; a check can't be dated in the future.</summary>
    [Fact]
    public void Checks_need_notes_for_an_issue_and_no_future_date()
    {
        Assert.Throws<ValidationException>(() => AssetRules.ValidateCheck(AssetStatus.Active, AssetCheckOutcome.IssueFound, Today, "  ", Today));
        Assert.Equal("Cracked screen", AssetRules.ValidateCheck(AssetStatus.Active, AssetCheckOutcome.IssueFound, Today, " Cracked screen ", Today));
        Assert.Null(AssetRules.ValidateCheck(AssetStatus.Active, AssetCheckOutcome.NotFound, Today.AddDays(-30), null, Today));
        Assert.Throws<ValidationException>(() => AssetRules.ValidateCheck(AssetStatus.Active, AssetCheckOutcome.Ok, Today.AddDays(1), null, Today));
    }

    /// <summary>
    /// AST-021: a quick-check scan is trimmed and required; it names the matches that aren't disposed, by name - one to check, several
    /// to choose from - and none, or only disposed ones, is refused.
    /// </summary>
    [Fact]
    public void Quick_check_scan_names_the_assets_that_can_be_checked()
    {
        Assert.Equal("SN-123", AssetRules.CleanScan("  SN-123 "));
        Assert.Throws<ValidationException>(() => AssetRules.CleanScan("   "));
        Assert.Throws<ValidationException>(() => AssetRules.CleanScan(null));
        Assert.Throws<ValidationException>(() => AssetRules.CleanScan(new string('9', 101)));

        var laptop = new QuickCheckCandidate(Guid.NewGuid(), "FA-1", "Reception laptop", AssetStatus.Active);
        var spare = new QuickCheckCandidate(Guid.NewGuid(), null, "Laptop spare", AssetStatus.InStorage);
        var scrapped = new QuickCheckCandidate(Guid.NewGuid(), "FA-2", "Old laptop", AssetStatus.Disposed);
        var lost = new QuickCheckCandidate(Guid.NewGuid(), null, "Missing laptop", AssetStatus.Lost);

        Assert.Throws<ValidationException>(() => AssetRules.QuickCheckTargets([], "SN-123"));
        Assert.Equal([laptop], AssetRules.QuickCheckTargets([laptop], "SN-123"));
        Assert.Equal([laptop], AssetRules.QuickCheckTargets([scrapped, laptop], "SN-123"));
        // A lost asset can be checked: finding it is a check.
        Assert.Equal([lost], AssetRules.QuickCheckTargets([lost], "SN-123"));
        Assert.Equal([spare, laptop], AssetRules.QuickCheckTargets([laptop, scrapped, spare], "SN-123"));
        var disposed = Assert.Throws<ValidationException>(() => AssetRules.QuickCheckTargets([scrapped], "SN-123"));
        Assert.Contains("FA-2 - Old laptop\" is disposed", disposed.Message);
        Assert.Contains("is disposed", Assert.Throws<ValidationException>(() => AssetRules.QuickCheckTargets([scrapped, scrapped with { Id = Guid.NewGuid() }], "SN-123")).Message);
    }

    /// <summary>AST-021: a repeat scan records nothing only when the same person already recorded an OK check on the asset today.</summary>
    [Fact]
    public void Quick_check_skips_only_my_own_ok_check_today()
    {
        var me = Guid.NewGuid();
        AssetCheck Check(Guid by, DateOnly on, AssetCheckOutcome outcome) => new() { CheckedById = by, CheckDate = on, Outcome = outcome };

        Assert.True(AssetRules.CheckedOkToday([Check(me, Today, AssetCheckOutcome.Ok)], me, Today));
        Assert.False(AssetRules.CheckedOkToday([], me, Today));
        Assert.False(AssetRules.CheckedOkToday([Check(me, Today, AssetCheckOutcome.NotFound)], me, Today));
        Assert.False(AssetRules.CheckedOkToday([Check(me, Today, AssetCheckOutcome.IssueFound)], me, Today));
        Assert.False(AssetRules.CheckedOkToday([Check(Guid.NewGuid(), Today, AssetCheckOutcome.Ok)], me, Today));
        Assert.False(AssetRules.CheckedOkToday([Check(me, Today.AddDays(-1), AssetCheckOutcome.Ok)], me, Today));
        Assert.False(AssetRules.CheckedOkToday([Check(me, Today, AssetCheckOutcome.Ok)], null, Today));
    }

    [Fact]
    public void Purchase_value_and_date_are_sensible()
    {
        Assert.Equal(18500.13m, AssetRules.CleanValue(18500.125m));
        Assert.Null(AssetRules.CleanValue(null));
        Assert.Throws<ValidationException>(() => AssetRules.CleanValue(-1m));
        Assert.Throws<ValidationException>(() => AssetRules.CleanPurchaseDate(Today.AddDays(1), Today));
        Assert.Equal(Today, AssetRules.CleanPurchaseDate(Today, Today));
    }
}
