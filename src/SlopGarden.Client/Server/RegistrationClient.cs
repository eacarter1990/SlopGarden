using System.Net;
using System.Net.Http.Json;
using SlopGarden.Client.Cameras;
using SlopGarden.Client.Infrastructure;
using SlopGarden.Contracts;

namespace SlopGarden.Client.Server;

/// <summary>Anonymous registration endpoints: request approval, then poll until an admin decides.</summary>
public sealed class RegistrationClient(ServerHttp http)
{
    public async Task<DeviceRegistrationResponse> RegisterAsync(Uri server, DeviceRegistrationRequest request, CancellationToken ct)
    {
        using var response = await http.Client.PostAsJsonAsync(new Uri(server, "api/device-api/register"), request, ClientJson.Options, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<DeviceRegistrationResponse>(ClientJson.Options, ct)
            ?? throw new InvalidOperationException("Empty registration response.");
    }

    /// <summary>Returns null when the server does not know the device id (e.g. deleted), so the client re-registers.</summary>
    public async Task<DeviceRegistrationStatus?> GetStatusAsync(Uri server, Guid deviceId, string secret, CancellationToken ct)
    {
        using var response = await http.Client.GetAsync(new Uri(server, $"api/device-api/register/{deviceId}?secret={Uri.EscapeDataString(secret)}"), ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<DeviceRegistrationStatus>(ClientJson.Options, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body)}", null, response.StatusCode);
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
