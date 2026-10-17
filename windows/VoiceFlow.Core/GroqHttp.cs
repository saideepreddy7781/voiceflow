using System.Globalization;
using System.Net;

namespace VoiceFlow.Core;

/// <summary>One shared HttpClient for every provider call, plus Groq's status handling.</summary>
public static class GroqHttp
{
    /// <summary>
    /// Shared client. Timeouts are per request (a linked CancellationTokenSource), so the client itself
    /// has none. Connections are recycled every few minutes so DNS changes are picked up.
    /// </summary>
    public static HttpClient Shared { get; } = CreateClient();

    static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VoiceFlow-Windows/0.1.0");
        return client;
    }

    /// <summary>Sends a request with a timeout and maps failures to <see cref="ProviderException"/>.</summary>
    public static async Task<byte[]> SendAsync(HttpClient client, Func<HttpRequestMessage> build, TimeSpan timeout,
                                               CancellationToken cancel)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(timeout);
        HttpResponseMessage response;
        using var request = build();
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw ProviderException.Network("the request timed out");
        }
        catch (HttpRequestException e)
        {
            throw ProviderException.Network(e.Message);
        }
        using (response)
        {
            byte[] data;
            try
            {
                data = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw ProviderException.Network("the response timed out");
            }
            catch (HttpRequestException e)
            {
                throw ProviderException.Network(e.Message);
            }
            switch ((int)response.StatusCode)
            {
                case 200:
                    return data;
                case 429:
                    throw ProviderException.RateLimited(RetryAfter(response));
                default:
                    throw ProviderException.Http((int)response.StatusCode, System.Text.Encoding.UTF8.GetString(data));
            }
        }
    }

    static double? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("retry-after", out var values))
        {
            var raw = values.FirstOrDefault();
            if (raw != null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return seconds;
        }
        return response.Headers.RetryAfter?.Delta?.TotalSeconds;
    }
}
