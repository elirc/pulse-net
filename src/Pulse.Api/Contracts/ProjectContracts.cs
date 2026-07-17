namespace Pulse.Api.Contracts;

public record CreateProjectRequest(string Name);

public record ProjectResponse(Guid Id, string Name, string ApiKey, DateTimeOffset CreatedAt);
