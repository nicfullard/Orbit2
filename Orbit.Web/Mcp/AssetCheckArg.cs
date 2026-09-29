using System.ComponentModel;

namespace Orbit.Mcp;

/// <summary>
/// One check on an asset: record_asset_check's arguments, and one entry of record_asset_checks' items (spec §6.19, §7.1).
/// </summary>
public sealed class AssetCheckArg
{
    [Description("Asset id (GUID), or its ERP asset number when it has one (required).")]
    public string? AssetId { get; set; }

    [Description("Ok (default), IssueFound (needs notes) or NotFound.")]
    public string? Outcome { get; set; }

    [Description("The day it was checked, yyyy-MM-dd; default today, never in the future.")]
    public string? CheckDate { get; set; }

    [Description("Notes - what the issue is, where it was found.")]
    public string? Notes { get; set; }
}
