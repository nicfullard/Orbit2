using Microsoft.AspNetCore.Http.Features;
using Orbit.Application;

namespace Orbit.Helpers;

/// <summary>Request-size plumbing for attachment uploads (spec §6.18).</summary>
public static class Uploads
{
    /// <summary>
    /// Raise the request body limit for an upload handler to what a full upload may need (files per upload x file size limit),
    /// since Kestrel's default is about 28 MB for the whole request. Must run before model binding reads the body - i.e. from
    /// <c>OnPageHandlerSelected</c>, not from the handler itself. The per-file limit is enforced in <c>AttachmentService</c>.
    /// </summary>
    public static void AllowUploadBody(HttpContext http, AttachmentOptions options)
    {
        var feature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = options.MaxUploadBodyBytes;
    }
}
