using System.Net;
using System.Text;

namespace FSOps.Server.Tests.Fakes;

/// <summary>
/// Stub transport for <see cref="HttpClient"/> so VatsimNetworkClient's failure paths (bad status,
/// malformed body, a fetch that throws) can be exercised deterministically. The real VATSIM feed
/// must never be hit from a test - the project polls VATSIM no more often than its 15-second
/// regeneration interval and treats hitting third-party infrastructure from a test suite as
/// against its own etiquette rules toward that infrastructure.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;

    public int CallCount { get; private set; }

    private FakeHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;

    public static FakeHttpMessageHandler WithJson(string json) =>
        new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));

    public static FakeHttpMessageHandler WithRawBody(string body) =>
        new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));

    public static FakeHttpMessageHandler WithStatus(HttpStatusCode status) =>
        new(_ => Task.FromResult(new HttpResponseMessage(status)));

    public static FakeHttpMessageHandler ThatThrows(Exception exception) =>
        new(_ => Task.FromException<HttpResponseMessage>(exception));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        cancellationToken.ThrowIfCancellationRequested();
        return _respond(request);
    }
}
