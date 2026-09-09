using System.Globalization;
using Pulse.Domain.Entities;

namespace Pulse.Api.Endpoints;

public static class FlagPrecondition
{
    public static string ETag(FeatureFlag flag) => $"\"flag-{flag.Id:D}-r{flag.Revision}\"";
    public static IResult? Require(HttpContext http, FeatureFlag flag, out long revision)
    {
        revision = 0;
        var values = http.Request.Headers.IfMatch;
        if (values.Count == 0) return Results.Problem("Read the flag and supply its ETag in If-Match.", statusCode: 428);
        if (values.Count != 1) return Invalid();
        var text = values[0]?.Trim();
        if (text is null || text.Length < 46 || !text.StartsWith("\"flag-", StringComparison.Ordinal) || text[^1] != '"' ||
            text[42..44] != "-r" || !Guid.TryParseExact(text[6..42], "D", out var id) ||
            !long.TryParse(text[44..^1], NumberStyles.None, CultureInfo.InvariantCulture, out revision) || revision < 1 ||
            text[44..^1] != revision.ToString(CultureInfo.InvariantCulture)) return Invalid();
        return id != flag.Id || revision != flag.Revision ? Results.Problem("The flag changed. Fetch it and reconcile your edit before retrying.", statusCode: 412) : null;
    }
    private static IResult Invalid() => Results.Problem("If-Match must contain one strong flag ETag; wildcards and lists are unsupported.", statusCode: 400);
}
