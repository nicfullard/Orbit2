using System.Text;
using Orbit.Mcp;

namespace Orbit.Tests.Mcp;

/// <summary>How get_attachment hands a file to the model (spec §6.18, §7.1): as text, as an image, or as a base64 blob.</summary>
public class AttachmentClassifierTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/csv; charset=utf-8")]
    [InlineData("TEXT/MARKDOWN")]
    [InlineData("application/json")]
    [InlineData("application/ld+json")]
    [InlineData("image/svg+xml")]
    [InlineData("application/octet-stream")]
    [InlineData("")]
    [InlineData(null)]
    public void Utf8_text_is_returned_as_text_when_the_type_allows_it(string? type) =>
        Assert.Equal(AttachmentContentKind.Text, AttachmentClassifier.Classify(type, Utf8("hello, wörld\nline 2\n")));

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg; q=1")]
    [InlineData("Image/GIF")]
    [InlineData("image/webp")]
    public void Raster_images_are_returned_as_images(string type) =>
        Assert.Equal(AttachmentContentKind.Image, AttachmentClassifier.Classify(type, PngBytes));

    [Fact]
    public void A_file_declared_as_text_that_is_not_utf8_is_binary()
    {
        Assert.Equal(AttachmentContentKind.Binary, AttachmentClassifier.Classify("text/plain", PngBytes));
        Assert.Equal(AttachmentContentKind.Binary, AttachmentClassifier.Classify("text/plain", Utf8("a\0b")));
        Assert.Equal(AttachmentContentKind.Binary, AttachmentClassifier.Classify("text/plain", []));
    }

    [Fact]
    public void A_file_of_a_named_binary_type_is_never_sniffed_as_text()
    {
        // A PDF or Word file that happens to be plain ASCII is still handed over as the file it says it is.
        Assert.Equal(AttachmentContentKind.Binary, AttachmentClassifier.Classify("application/pdf", Utf8("%PDF-1.4 plain ascii")));
        Assert.Equal(AttachmentContentKind.Binary,
            AttachmentClassifier.Classify("application/vnd.openxmlformats-officedocument.wordprocessingml.document", Utf8("PK")));
    }

    [Fact]
    public void An_untyped_file_is_binary_unless_it_looks_like_text()
    {
        Assert.Equal(AttachmentContentKind.Binary, AttachmentClassifier.Classify("application/octet-stream", PngBytes));
        Assert.Equal(AttachmentContentKind.Text, AttachmentClassifier.Classify("application/octet-stream", Utf8("id,name\n1,Ann\n")));
    }

    [Fact]
    public void Decoding_drops_the_byte_order_mark()
    {
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8("x = 1")).ToArray();
        Assert.Equal("x = 1", AttachmentClassifier.DecodeText(withBom));
        Assert.Equal("x = 1", AttachmentClassifier.DecodeText(Utf8("x = 1")));
    }

    [Theory]
    [InlineData("text/plain; charset=utf-8", "text/plain")]
    [InlineData(" Image/PNG ", "image/png")]
    [InlineData(null, "")]
    public void Media_type_is_the_type_alone_lower_cased(string? raw, string expected) =>
        Assert.Equal(expected, AttachmentClassifier.MediaType(raw));
}
