namespace Pulse.Api.Endpoints;

/// <summary>Small HTTP input rules shared by list and update handlers.</summary>
internal static class InputRules
{
    public static IResult Problem(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    public static IResult? Text(string? raw, int maximum, string field, out string? value, bool required = false)
    {
        value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        if (required && value is null)
            return Problem(field, $"{field} is required.");
        return value?.Length > maximum
            ? Problem(field, $"{field} must contain at most {maximum} characters after trimming.")
            : null;
    }

    public static IResult? Choice(string? raw, string field, string[] allowed, out string? value)
    {
        value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().ToLowerInvariant();
        return value is not null && !allowed.Contains(value)
            ? Problem(field, $"{field} must be one of: {string.Join(", ", allowed)}.")
            : null;
    }
}
