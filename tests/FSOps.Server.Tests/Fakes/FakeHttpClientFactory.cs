namespace FSOps.Server.Tests.Fakes;

/// <summary>Hands out <see cref="HttpClient"/>s backed by a single fake handler, regardless of the
/// named client requested - enough for VatsimNetworkClientTests, which only ever asks for one
/// name and cares about the requests/responses, not client-name routing.</summary>
internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}
