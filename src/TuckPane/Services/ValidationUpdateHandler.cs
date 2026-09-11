#if TUCKPANE_UPDATE_VALIDATION
using System.Net;
using System.Net.Http;
using TuckPane.Updates;

namespace TuckPane.Services;

// Compiled exclusively into the isolated user-validation builds. Production has no feed override.
internal sealed class ValidationUpdateHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT") ?? throw new IOException("Missing validation data root.");
        string name = request.RequestUri!.AbsoluteUri == ReleaseRules.LatestUrl ? "release.json" : Path.GetFileName(request.RequestUri.LocalPath);
        if (name is not ("release.json" or "SHA256SUMS.txt" or "TuckPane-3.1.0-win-x64-setup.exe" or "TuckPane-3.1.0-win-x64-portable.zip"))
            throw new IOException("Unknown validation asset.");
        string path = Path.Combine(root, "update-feed", name);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(path)), RequestMessage = request };
        response.Content.Headers.ContentLength = new FileInfo(path).Length;
        return Task.FromResult(response);
    }
}
#endif
