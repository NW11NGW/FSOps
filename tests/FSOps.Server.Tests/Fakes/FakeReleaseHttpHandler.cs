using System.Net;
using System.Text;

namespace FSOps.Server.Tests.Fakes;

/// <summary>
/// A routed stub transport for the update checker's tests. The updater makes up to three different
/// requests (release metadata, the checksum sidecar, the installer bytes) and the interesting
/// behaviour is almost entirely about what happens when one of them misbehaves while the others do
/// not - so responses are matched per URL rather than the single canned response
/// <see cref="FakeHttpMessageHandler"/> provides.
/// <para>
/// GitHub is never contacted from a test. Every path the updater has, including the network being
/// down, must pass with no internet connection at all.
/// </para>
/// </summary>
internal sealed class FakeReleaseHttpHandler : HttpMessageHandler
{
    private readonly List<Route> _routes = new();

    /// <summary>Every URL requested, in order. Used to prove the kill switch really makes NO
    /// outbound request, rather than making one and discarding the answer.</summary>
    public List<string> RequestedUrls { get; } = new();

    public int CallCount => RequestedUrls.Count;

    public FakeReleaseHttpHandler When(string urlContains, Func<HttpResponseMessage> respond)
    {
        _routes.Add(new Route(urlContains, _ => Task.FromResult(respond())));
        return this;
    }

    public FakeReleaseHttpHandler WhenJson(string urlContains, string json) =>
        When(urlContains, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    public FakeReleaseHttpHandler WhenText(string urlContains, string text) =>
        When(urlContains, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(text, Encoding.UTF8, "text/plain"),
        });

    public FakeReleaseHttpHandler WhenBytes(string urlContains, byte[] bytes) =>
        When(urlContains, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        });

    public FakeReleaseHttpHandler WhenStatus(string urlContains, HttpStatusCode status) =>
        When(urlContains, () => new HttpResponseMessage(status));

    /// <summary>A 403 carrying GitHub's rate-limit headers, which is what an over-eager or shared
    /// IP address actually gets back.</summary>
    public FakeReleaseHttpHandler WhenRateLimited(string urlContains) =>
        When(urlContains, () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("x-ratelimit-remaining", "0");
            response.Headers.Add("x-ratelimit-limit", "60");
            return response;
        });

    public FakeReleaseHttpHandler WhenThrows(string urlContains, Exception exception)
    {
        _routes.Add(new Route(urlContains, _ => Task.FromException<HttpResponseMessage>(exception)));
        return this;
    }

    /// <summary>A request that never answers - stands in for a hung connection, and is resolved by
    /// the client's own request timeout rather than by the test waiting for one.</summary>
    public FakeReleaseHttpHandler WhenHangs(string urlContains)
    {
        _routes.Add(new Route(urlContains, async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new UnreachableException();
        }));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.ToString() ?? string.Empty;
        RequestedUrls.Add(url);
        cancellationToken.ThrowIfCancellationRequested();

        var route = _routes.FirstOrDefault(r => url.Contains(r.UrlContains, StringComparison.OrdinalIgnoreCase));
        if (route is null)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        return await route.Respond(cancellationToken);
    }

    private sealed record Route(string UrlContains, Func<CancellationToken, Task<HttpResponseMessage>> Respond);

    private sealed class UnreachableException : Exception;
}
