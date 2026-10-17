using System.Net;
using System.Net.Http;
using Resona.Services;

namespace Resona.Tests;

public sealed class TrackAnalysisServiceTests
{
    [Fact]
    public void Upload_duration_is_limited_to_seven_minutes() =>
        Assert.Equal(420, TrackAnalysisService.MaximumUploadDurationSeconds);

    [Theory]
    [InlineData("http://192.168.178.102:5081/health", "http://192.168.178.102:5081")]
    [InlineData("https://analyzer.example.test/HEALTH/", "https://analyzer.example.test")]
    [InlineData("https://analyzer.example.test/prefix/", "https://analyzer.example.test/prefix")]
    public void Server_address_is_normalized(string value, string expected)
    {
        Assert.True(TrackAnalysisService.TryNormalizeServerUrl(value, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public async Task CheckHealthAsync_SendsRequest()
    {
        var handler = new CapturingHandler("{\"status\":\"ok\"}");
        using var service = CreateService(handler);

        var healthy = await service.CheckHealthAsync();

        Assert.True(healthy);
        Assert.Equal(HttpMethod.Get, handler.Method);
    }

    [Fact]
    public async Task AnalyzeTrackAsync_SendsRequest()
    {
        var trackPath = Path.Combine(Path.GetTempPath(), $"resona-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(trackPath, [0]);
        try
        {
            var handler = new CapturingHandler("{\"success\":true}");
            using var service = CreateService(handler);

            var result = await service.AnalyzeTrackAsync(trackPath);

            Assert.True(result.Success);
            Assert.Equal(HttpMethod.Post, handler.Method);
        }
        finally
        {
            File.Delete(trackPath);
        }
    }

    [Fact]
    public async Task Server_error_preserves_http_status_code()
    {
        var handler = new CapturingHandler("{}", HttpStatusCode.InternalServerError);
        using var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<MusicAnalysisException>(() => service.CheckHealthAsync());

        Assert.Equal(MusicAnalysisErrorKind.ServerError, exception.Kind);
        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
    }

    private static TrackAnalysisService CreateService(CapturingHandler handler) =>
        new(
            new HttpClient(handler),
            serverUrlProvider: () => "https://analyzer.test");

    private sealed class CapturingHandler(
        string responseBody,
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody)
            });
        }
    }
}
