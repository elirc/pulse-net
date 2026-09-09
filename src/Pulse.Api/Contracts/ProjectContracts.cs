namespace Pulse.Api.Contracts;

public record RoleProjectResponse(Guid Id, string Name, string Role, DateTimeOffset CreatedAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ApiKey,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ReadKey);

public record ChangeMemberRoleRequest(string? Role);
public record RotateProjectReadKeyRequest(string? ExpectedReadKey);

public record CreateProjectRequest(string Name);

public record UpdateProjectRequest(string? Name);

public record ProjectResponse(Guid Id, string Name, string ApiKey, string ReadKey, DateTimeOffset CreatedAt);
