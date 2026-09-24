using Orbit.Application;
using Orbit.Application.Assets;
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

    /// <summary>AST-016: an issue found needs notes; a check can't be dated in the future.</summary>
    [Fact]
    public void Checks_need_notes_for_an_issue_and_no_future_date()
    {
        Assert.Throws<ValidationException>(() => AssetRules.ValidateCheck(AssetStatus.Active, AssetCheckOutcome.IssueFound, Today, "  ", Today));
        Assert.Equal("Cracked screen", AssetRules.ValidateCheck(AssetStatus.Active, AssetCheckOutcome.IssueFound, Today, " Cracked screen ", Today));
        Assert.Null(AssetRules.ValidateCheck(AssetStatus.Active, AssetCheckOutcome.NotFound, Today.AddDays(-30), null, Today));
        Assert.Throws<ValidationException>(() => AssetRules.ValidateCheck(AssetStatus.Active, AssetCheckOutcome.Ok, Today.AddDays(1), null, Today));
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
