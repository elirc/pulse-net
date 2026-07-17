using System.Net.Http.Headers;
using System.Net.Http.Json;
using Pulse.Api.Contracts;

namespace Pulse.Tests;

/// <summary>Registers throwaway users against the auth API for tests.</summary>
public static class TestAuth
{
    public const string Password = "correct-horse-battery";

    /// <summary>Registers a fresh user and returns its bearer token and email.</summary>
    public static async Task<(string Token, string Email)> RegisterAsync(
        HttpClient client, string? email = null)
    {
        email ??= $"user-{Guid.NewGuid():N}@test.dev";

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = Password,
            name = "Test User",
        });
        response.EnsureSuccessStatusCode();

        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        return (auth.Token, email);
    }

    /// <summary>
    /// Registers a fresh user and installs its token as the client's default
    /// Authorization header. No-op if the client is already authenticated.
    /// </summary>
    public static async Task AuthenticateAsync(HttpClient client)
    {
        if (client.DefaultRequestHeaders.Authorization is not null)
        {
            return;
        }

        var (token, _) = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
