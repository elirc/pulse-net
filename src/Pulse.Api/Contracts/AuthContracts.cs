namespace Pulse.Api.Contracts;

public record RegisterRequest(string? Email, string? Password, string? Name);

public record LoginRequest(string? Email, string? Password);

public record UserResponse(Guid Id, string Email, string Name, DateTimeOffset CreatedAt);

public record AuthResponse(string Token, DateTimeOffset ExpiresAt, UserResponse User);

public record CreatePersonalApiKeyRequest(string? Name);

/// <summary>Returned once at creation — the only time the plaintext key exists.</summary>
public record PersonalApiKeyCreatedResponse(Guid Id, string Name, string Key, DateTimeOffset CreatedAt);

public record PersonalApiKeyResponse(Guid Id, string Name, string KeySuffix, DateTimeOffset CreatedAt);

public record AddMemberRequest(string? Email);

public record MemberResponse(Guid UserId, string Email, string Name, DateTimeOffset AddedAt);
