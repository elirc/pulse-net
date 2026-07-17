namespace Pulse.Api.Contracts;

public record CreateAnnotationRequest(DateOnly? Date, string? Content);

public record UpdateAnnotationRequest(DateOnly? Date, string? Content);

public record AnnotationResponse(
    Guid Id,
    Guid ProjectId,
    DateOnly Date,
    string Content,
    DateTimeOffset CreatedAt);

public record EventDefinitionResponse(
    string Name,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

public record PropertyDefinitionResponse(
    string Name,
    string PropertyType,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

/// <summary>Receipt for a GDPR-style person purge.</summary>
public record PersonDeletionResponse(
    Guid PersonId,
    int DeletedEvents,
    int DeletedDistinctIds);
