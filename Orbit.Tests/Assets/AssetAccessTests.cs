using Orbit.Application;
using Orbit.Application.Assets;
using Orbit.Data.Entities;
using Orbit.Tests.Access;

namespace Orbit.Tests.Assets;

/// <summary>
/// Who may see, register, edit, move, check and configure assets (spec §6.19). Department is the asset's managing department;
/// Own is holding it - an AssetAssignment row - wherever it is managed.
/// </summary>
public class AssetAccessTests
{
    private static readonly Guid It = Guid.NewGuid();
    private static readonly Guid Marketing = Guid.NewGuid();

    private static Asset Asset(Guid department, params Guid?[] holders)
    {
        var asset = new Asset { DepartmentId = department, AssetNumber = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Laptop" };
        foreach (var h in holders.OfType<Guid>()) asset.Assignments.Add(new AssetAssignment { AssetId = asset.Id, UserId = h });
        return asset;
    }

    private static bool CanView(Actor a, Asset asset) => AccessPolicy.CanViewAsset(a, asset, AccessPolicy.IsAssigned(a, asset));
    private static bool CanCheck(Actor a, Asset asset) => AccessPolicy.CanCheckAsset(a, asset, AccessPolicy.IsAssigned(a, asset));

    /// <summary>AST-001: Own = held; Department = managed by the department, plus held elsewhere; All = everything; none = nothing.</summary>
    [Fact]
    public void View_follows_holding_the_managing_department_or_everything()
    {
        var me = Guid.NewGuid();
        Actor Viewer(PermissionScope scope) => TestActors.With(new Dictionary<string, PermissionScope> { [Permission.AssetsView] = scope }, Marketing, userId: me);
        var itLaptopIHold = Asset(It, me);
        var itLaptop = Asset(It, Guid.NewGuid());
        var marketingProjector = Asset(Marketing);
        var list = new List<Asset> { itLaptopIHold, itLaptop, marketingProjector };
        string[] Names(IEnumerable<Asset> assets) => assets.Select(a => a.AssetNumber!).OrderBy(n => n).ToArray();

        Assert.Equal(Names(list), Names(Scoping.Assets(list.AsQueryable(), Viewer(PermissionScope.All))));
        Assert.Equal(Names([itLaptopIHold, marketingProjector]), Names(Scoping.Assets(list.AsQueryable(), Viewer(PermissionScope.Department))));
        Assert.Equal(Names([itLaptopIHold]), Names(Scoping.Assets(list.AsQueryable(), Viewer(PermissionScope.Own))));
        Assert.Empty(Scoping.Assets(list.AsQueryable(), TestActors.Nobody(Marketing)));

        var member = Viewer(PermissionScope.Own);
        Assert.True(CanView(member, itLaptopIHold));
        Assert.False(CanView(member, itLaptop));
        Assert.False(CanView(member, marketingProjector));
        Assert.True(CanView(Viewer(PermissionScope.Department), itLaptopIHold));
        Assert.False(CanView(Viewer(PermissionScope.Department), itLaptop));
    }

    [Fact]
    public void Registering_an_asset_does_not_make_it_your_own()
    {
        var clerk = TestActors.Grants(Marketing, (Permission.AssetsView, PermissionScope.Own));
        var registered = Asset(It);
        Assert.False(AccessPolicy.IsAssigned(clerk, registered));
        Assert.False(CanView(clerk, registered));
    }

    /// <summary>AST-002: editing reaches the department's assets only, and a move needs the target department too.</summary>
    [Fact]
    public void Edit_and_move_need_the_department_or_all()
    {
        var itAdmin = TestActors.DepartmentAdmin(It);
        Assert.True(AccessPolicy.CanEditAsset(itAdmin, Asset(It)));
        Assert.False(AccessPolicy.CanEditAsset(itAdmin, Asset(Marketing)));
        Assert.False(AccessPolicy.CanMoveAssetTo(itAdmin, Marketing));
        Assert.True(AccessPolicy.CanMoveAssetTo(itAdmin, It));

        var assetManager = TestActors.Grants(null, (Permission.AssetsEdit, PermissionScope.All));
        Assert.True(AccessPolicy.CanEditAsset(assetManager, Asset(Marketing)));
        Assert.True(AccessPolicy.CanMoveAssetTo(assetManager, Marketing));

        // Holding an asset never lets you edit its record.
        var member = TestActors.Member(Marketing);
        Assert.False(AccessPolicy.CanEditAsset(member, Asset(It, member.UserId)));
    }

    /// <summary>AST-003: registering reaches the department, or anywhere at All.</summary>
    [Fact]
    public void Register_in_own_department_or_anywhere()
    {
        var itAdmin = TestActors.DepartmentAdmin(It);
        Assert.True(AccessPolicy.CanCreateAssetIn(itAdmin, It));
        Assert.False(AccessPolicy.CanCreateAssetIn(itAdmin, Marketing));
        Assert.False(AccessPolicy.CanCreateAssetIn(TestActors.Member(It), It));
        Assert.True(AccessPolicy.CanCreateAssetIn(TestActors.SystemAdmin(), Marketing));
    }

    /// <summary>AST-004: checks at Own are the holder's; removing a check takes its recorder or an editor.</summary>
    [Fact]
    public void Check_own_held_assets_and_remove_only_your_own_checks()
    {
        var member = TestActors.Member(Marketing);
        var mine = Asset(It, member.UserId);
        var notMine = Asset(It, Guid.NewGuid());
        Assert.True(CanCheck(member, mine));
        Assert.False(CanCheck(member, notMine));
        Assert.True(CanCheck(TestActors.DepartmentAdmin(It), notMine));
        Assert.False(CanCheck(TestActors.DepartmentAdmin(Marketing), notMine));

        var ownCheck = new AssetCheck { AssetId = mine.Id, CheckedById = member.UserId };
        var othersCheck = new AssetCheck { AssetId = mine.Id, CheckedById = Guid.NewGuid() };
        Assert.True(AccessPolicy.CanRemoveCheck(member, ownCheck, mine));
        Assert.False(AccessPolicy.CanRemoveCheck(member, othersCheck, mine));
        Assert.True(AccessPolicy.CanRemoveCheck(TestActors.DepartmentAdmin(It), othersCheck, mine));
    }

    [Fact]
    public void Comments_and_files_follow_viewing_deleting_a_file_takes_its_uploader_or_an_editor()
    {
        var member = TestActors.Member(Marketing);
        var mine = Asset(It, member.UserId);
        Assert.True(AccessPolicy.CanCommentOnAsset(member, mine, true));
        Assert.True(AccessPolicy.CanAttachToAsset(member, mine, true));
        Assert.False(AccessPolicy.CanAttachToAsset(member, Asset(It), false));

        var photo = new Attachment { AssetId = mine.Id, UploadedById = member.UserId };
        var invoice = new Attachment { AssetId = mine.Id, UploadedById = Guid.NewGuid() };
        Assert.True(AccessPolicy.CanDeleteAttachment(member, photo, mine));
        Assert.False(AccessPolicy.CanDeleteAttachment(member, invoice, mine));
        Assert.True(AccessPolicy.CanDeleteAttachment(TestActors.DepartmentAdmin(It), invoice, mine));
    }

    /// <summary>AST-017: an asset's type and location are its managing department's own.</summary>
    [Fact]
    public void Type_and_location_must_be_the_departments_own()
    {
        var itLaptop = new AssetType { DepartmentId = It, Name = "Laptop" };
        var marketingLaptop = new AssetType { DepartmentId = Marketing, Name = "Laptop" };
        var serverRoom = new AssetLocation { DepartmentId = It, Name = "Server room" };
        var studio = new AssetLocation { DepartmentId = Marketing, Name = "Studio" };

        Assert.True(AccessPolicy.CanUseAssetType(It, itLaptop));
        Assert.False(AccessPolicy.CanUseAssetType(It, marketingLaptop));
        Assert.True(AccessPolicy.CanUseAssetLocation(It, serverRoom));
        Assert.False(AccessPolicy.CanUseAssetLocation(It, studio));

        AssetRules.CheckTypeAndLocation(It, "IT", itLaptop, serverRoom, moving: false);
        AssetRules.CheckTypeAndLocation(It, "IT", itLaptop, null, moving: false);
        Assert.Throws<ValidationException>(() => AssetRules.CheckTypeAndLocation(It, "IT", marketingLaptop, null, moving: false));
        Assert.Throws<ValidationException>(() => AssetRules.CheckTypeAndLocation(It, "IT", itLaptop, studio, moving: false));
    }

    /// <summary>AST-018 (the rule half): a move needs a type of the new department, and a location there or none.</summary>
    [Fact]
    public void A_move_needs_the_new_departments_type_and_location_or_none()
    {
        var itLaptop = new AssetType { DepartmentId = It, Name = "Laptop" };
        var marketingLaptop = new AssetType { DepartmentId = Marketing, Name = "Laptop" };
        var serverRoom = new AssetLocation { DepartmentId = It, Name = "Server room" };

        var noType = Assert.Throws<ValidationException>(() => AssetRules.CheckTypeAndLocation(Marketing, "Marketing", itLaptop, null, moving: true));
        Assert.Contains("Moving the asset to Marketing", noType.Message);
        var oldLocation = Assert.Throws<ValidationException>(() => AssetRules.CheckTypeAndLocation(Marketing, "Marketing", marketingLaptop, serverRoom, moving: true));
        Assert.Contains("or none", oldLocation.Message);
        AssetRules.CheckTypeAndLocation(Marketing, "Marketing", marketingLaptop, null, moving: true);
    }

    /// <summary>AST-019: configuring types and locations reaches the department, or every department at All.</summary>
    [Fact]
    public void Configure_types_and_locations_in_own_department_or_everywhere()
    {
        var itAdmin = TestActors.DepartmentAdmin(It);
        Assert.True(AccessPolicy.CanConfigureAssetsIn(itAdmin, It));
        Assert.False(AccessPolicy.CanConfigureAssetsIn(itAdmin, Marketing));
        Assert.False(AccessPolicy.CanConfigureAssetsIn(TestActors.Member(It), It));
        Assert.True(AccessPolicy.CanConfigureAssetsIn(TestActors.SystemAdmin(), Marketing));

        var types = new List<AssetType> { new() { DepartmentId = It, Name = "Laptop" }, new() { DepartmentId = Marketing, Name = "Camera" } };
        var locations = new List<AssetLocation> { new() { DepartmentId = It, Name = "Store" }, new() { DepartmentId = Marketing, Name = "Studio" } };
        Assert.Equal(["Laptop"], Scoping.AssetTypes(types.AsQueryable(), itAdmin).Select(t => t.Name).ToArray());
        Assert.Equal(["Store"], Scoping.AssetLocations(locations.AsQueryable(), itAdmin).Select(l => l.Name).ToArray());
        Assert.Equal(2, Scoping.AssetTypes(types.AsQueryable(), TestActors.SystemAdmin()).Count());
        Assert.Empty(Scoping.AssetLocations(locations.AsQueryable(), TestActors.Member(It)));
    }

    /// <summary>AST-015 (the defaults half): Members see and self-certify what they hold; Department Admins run their department's register.</summary>
    [Fact]
    public void Shipped_roles_default_asset_grants()
    {
        Assert.Equal(PermissionScope.Own, DefaultRoles.MemberGrants[Permission.AssetsView]);
        Assert.Equal(PermissionScope.Own, DefaultRoles.MemberGrants[Permission.AssetsCheck]);
        Assert.False(DefaultRoles.MemberGrants.ContainsKey(Permission.AssetsCreate));
        Assert.False(DefaultRoles.MemberGrants.ContainsKey(Permission.AssetsEdit));
        Assert.False(DefaultRoles.MemberGrants.ContainsKey(Permission.AssetsConfigure));
        foreach (var key in new[] { Permission.AssetsView, Permission.AssetsCreate, Permission.AssetsEdit, Permission.AssetsCheck, Permission.AssetsConfigure })
            Assert.Equal(PermissionScope.Department, DefaultRoles.DepartmentAdminGrants[key]);
    }
}
