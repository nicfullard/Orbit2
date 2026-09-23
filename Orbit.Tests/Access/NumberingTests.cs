using Orbit.Application.Services;

namespace Orbit.Tests.Access;

/// <summary>Human-readable numbers (spec §5.1): T-26-00012 / P-26-00003.</summary>
public class NumberingTests
{
    [Theory]
    [InlineData("T", 2026, 1, "T-26-00001")]
    [InlineData("P", 2026, 12, "P-26-00012")]
    [InlineData("T", 2031, 99999, "T-31-99999")]
    [InlineData("T", 2026, 100000, "T-26-100000")]
    public void Numbers_are_prefix_two_digit_year_and_a_five_digit_counter_that_can_grow(string prefix, int year, int sequence, string expected)
    {
        Assert.Equal(expected, NumberingService.Format(prefix, year, sequence));
    }

    [Theory]
    [InlineData("T-26-00001", true)]
    [InlineData(" p-26-00003 ", true)]
    [InlineData("T-26-123456", true)]
    [InlineData("T-26-0001", false)]
    [InlineData("X-26-00001", false)]
    [InlineData("T26-00001", false)]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_number_is_recognised_in_any_case_and_told_apart_from_a_guid(string? text, bool expected)
    {
        Assert.Equal(expected, NumberingService.IsNumber(text));
    }

    [Fact]
    public void Normalising_trims_and_upper_cases()
    {
        Assert.Equal("T-26-00001", NumberingService.Normalise(" t-26-00001 "));
    }
}
