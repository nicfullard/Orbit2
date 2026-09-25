using Orbit.Data.Entities;
using Orbit.Pages.Assets;

namespace Orbit.Tests.Assets;

/// <summary>The asset form filled from another asset: the asset page's Copy (spec §6.19).</summary>
public class AssetFormTests
{
    /// <summary>
    /// AST-022: a copy keeps every detail and property value but not what belongs to the one item - the ERP asset number, the
    /// serial number and the holders; a copy of a disposed asset starts Active with no disposal date.
    /// </summary>
    [Fact]
    public void A_copy_keeps_the_details_and_clears_what_identifies_the_item()
    {
        var propertyId = Guid.NewGuid();
        var source = new Asset
        {
            DepartmentId = Guid.NewGuid(), AssetNumber = "FA-004211", Name = "Reception laptop", Description = "14-inch, docked",
            AssetTypeId = Guid.NewGuid(), Manufacturer = "Lenovo", Model = "T14", SerialNumber = "PF3ABC12",
            Status = AssetStatus.Disposed, DisposedOn = new DateOnly(2026, 9, 1), AssetLocationId = Guid.NewGuid(),
            PurchaseDate = new DateOnly(2024, 2, 1), PurchaseValue = 18500.5m, PurchaseOrder = "PO-77", InvoiceNumber = "INV-9",
            Supplier = "Acme IT", WarrantyExpiresOn = new DateOnly(2027, 2, 1)
        };
        source.PropertyValues.Add(new AssetPropertyValue { AssetTypePropertyId = propertyId, Value = "16" });
        source.Assignments.Add(new AssetAssignment { UserId = Guid.NewGuid() });

        var copy = AssetForm.CopyOf(source);

        Assert.Null(copy.AssetNumber);
        Assert.Null(copy.SerialNumber);
        Assert.Empty(copy.AssigneeIds);
        Assert.Equal(AssetStatus.Active, copy.Status);
        Assert.Null(copy.DisposedOn);

        Assert.Equal(source.Name, copy.Name);
        Assert.Equal(source.Description, copy.Description);
        Assert.Equal(source.DepartmentId, copy.DepartmentId);
        Assert.Equal(source.AssetTypeId, copy.AssetTypeId);
        Assert.Equal(source.Manufacturer, copy.Manufacturer);
        Assert.Equal(source.Model, copy.Model);
        Assert.Equal(source.AssetLocationId, copy.AssetLocationId);
        Assert.Equal(source.PurchaseDate, copy.PurchaseDate);
        Assert.Equal("18500.50", copy.PurchaseValue);
        Assert.Equal(source.PurchaseOrder, copy.PurchaseOrder);
        Assert.Equal(source.InvoiceNumber, copy.InvoiceNumber);
        Assert.Equal(source.Supplier, copy.Supplier);
        Assert.Equal(source.WarrantyExpiresOn, copy.WarrantyExpiresOn);
        Assert.Equal("16", Assert.Single(copy.Properties, p => p.Key == propertyId.ToString()).Value);
    }

    /// <summary>AST-022: any status but Disposed is copied as it is.</summary>
    [Theory]
    [InlineData(AssetStatus.Active)]
    [InlineData(AssetStatus.InStorage)]
    [InlineData(AssetStatus.Damaged)]
    [InlineData(AssetStatus.Lost)]
    public void A_copy_keeps_any_status_but_disposed(AssetStatus status)
    {
        var copy = AssetForm.CopyOf(new Asset { DepartmentId = Guid.NewGuid(), Name = "Spare phone", Status = status });

        Assert.Equal(status, copy.Status);
    }
}
