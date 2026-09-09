using System.Text.RegularExpressions;
using Pulse.Domain.Entities;

namespace Pulse.Api.Auth;

public record ProjectPermission(ProjectRole MinimumRole, string? Scope, bool AllowsReadKey = false);

/// <summary>Explicit route classifications; scopes are used by restricted-token enforcement.</summary>
public static class ProjectPermissionMatrix
{
    public static IReadOnlyDictionary<string, ProjectPermission> Rules { get; } = Build();
    public static string Normalize(string method, string route) => method.ToUpperInvariant() + " " +
        Regex.Replace(route, "\\{[^}]+\\}", "{}").TrimEnd('/');

    public static ProjectPermission? Find(HttpContext http)
    {
        var route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        return route is not null && Rules.TryGetValue(Normalize(http.Request.Method, route), out var rule) ? rule : null;
    }

    private static Dictionary<string, ProjectPermission> Build()
    {
        var rules = new Dictionary<string, ProjectPermission>(StringComparer.OrdinalIgnoreCase);
        void Add(string method, string suffix, ProjectRole role, string? scope, bool readKey = false) =>
            rules.Add(Normalize(method, "/api/projects/{}" + suffix), new(role, scope, readKey));
        const ProjectRole viewer = ProjectRole.Viewer, editor = ProjectRole.Editor, admin = ProjectRole.Admin;
        const string analytics = "analytics:read", read = "configuration:read", write = "configuration:write", exports = "exports:write";
        Add("GET", "", viewer, read);
        Add("GET", "/overview", viewer, analytics);
        Add("GET", "/alert-rules", viewer, read);
        Add("GET", "/alert-rules/{}", viewer, read);
        Add("POST", "/alert-rules", editor, write);
        Add("PUT", "/alert-rules/{}", editor, write);
        Add("DELETE", "/alert-rules/{}", editor, write);
        Add("GET", "/notifications", viewer, read);
        Add("PUT", "/notifications/{}/read", viewer, read);
        Add("GET", "/event-usage", viewer, analytics);
        Add("GET", "/property-values", viewer, analytics);
        Add("GET", "/ingestion/status", viewer, analytics);
        Add("PUT", "", admin, null);
        Add("POST", "/read-key/rotate", admin, null);
        Add("GET", "/members", viewer, null);
        Add("GET", "/audit", admin, null);
        Add("GET", "/retention", admin, null);
        Add("PUT", "/retention", admin, null);
        Add("GET", "/retention/preview", admin, null);
        Add("GET", "/retention/runs", admin, null);
        Add("POST", "/members", admin, null);
        Add("PUT", "/members/{}/role", admin, null);
        Add("DELETE", "/members/{}", admin, null);
        Add("GET", "/persons", viewer, analytics);
        Add("GET", "/persons/count", viewer, analytics);
        Add("GET", "/persons/{}", viewer, analytics);
        Add("GET", "/persons/{}/events", viewer, analytics);
        Add("GET", "/persons/{}/activity-summary", viewer, analytics);
        Add("GET", "/persons/{}/sessions", viewer, analytics);
        Add("GET", "/persons/by-distinct-id/{}", viewer, analytics);
        Add("DELETE", "/persons/{}", admin, null);
        Add("POST", "/persons/{}/erasure", admin, null);
        Add("GET", "/erasure-jobs/{}", admin, null);
        Add("POST", "/erasure-jobs/{}/resume", admin, null);
        Add("POST", "/erasure-jobs/{}/discard-unreadable", admin, null);
        Add("GET", "/insights/trend", viewer, analytics, true);
        Add("GET", "/insights/trend-bounded", viewer, analytics, true);
        Add("POST", "/insights/funnel", viewer, analytics, true);
        Add("GET", "/insights/retention", viewer, analytics, true);
        Add("GET", "/insights/period-comparison", viewer, analytics, true);
        Add("POST", "/insights/multi-trend", viewer, analytics, true);
        Add("POST", "/insights/preview", viewer, analytics);
        Add("GET", "/insights", viewer, read);
        Add("GET", "/insights/{}", viewer, read);
        Add("POST", "/insights", editor, write);
        Add("PUT", "/insights/{}", editor, write);
        Add("DELETE", "/insights/{}", editor, write);
        Add("GET", "/insights/{}/usages", viewer, read);
        Add("GET", "/dashboards", viewer, read);
        Add("GET", "/dashboards/{}", viewer, read);
        Add("POST", "/dashboards", editor, write);
        Add("PUT", "/dashboards/{}", editor, write);
        Add("DELETE", "/dashboards/{}", editor, write);
        Add("POST", "/dashboards/{}/tiles", editor, write);
        Add("PUT", "/dashboards/{}/tiles/{}", editor, write);
        Add("DELETE", "/dashboards/{}/tiles/{}", editor, write);
        Add("PUT", "/dashboards/{}/tile-layouts", editor, write);
        Add("POST", "/dashboards/{}/duplicate", editor, write);
        Add("GET", "/dashboards/{}/template", viewer, read);
        Add("POST", "/dashboards/import", editor, write);
        Add("POST", "/dashboards/{}/refresh", viewer, analytics);
        Add("POST", "/dashboards/{}/refresh-selection", viewer, analytics);
        Add("GET", "/cohorts", viewer, read);
        Add("POST", "/cohorts/preview", viewer, analytics);
        Add("PUT", "/cohorts/{}/rules", editor, write);
        Add("POST", "/cohorts/{}/snapshot", editor, write);
        Add("PUT", "/cohorts/{}/persons", editor, write);
        Add("GET", "/cohorts/{}", viewer, read);
        Add("POST", "/cohorts", editor, write);
        Add("DELETE", "/cohorts/{}", editor, write);
        Add("GET", "/cohorts/{}/persons", viewer, analytics);
        Add("POST", "/cohorts/{}/persons", editor, write);
        Add("DELETE", "/cohorts/{}/persons/{}", editor, write);
        Add("GET", "/feature-flags", viewer, read);
        Add("POST", "/feature-flags/{}/explain", viewer, read);
        Add("POST", "/feature-flags/{}/clone", editor, write);
        Add("POST", "/feature-flags/evaluate-batch", viewer, read);
        Add("GET", "/feature-flags/{}", viewer, read);
        Add("GET", "/feature-flags/local-evaluation", viewer, read, true);
        Add("POST", "/feature-flags", editor, write);
        Add("GET", "/feature-flags/{}/versions", viewer, read);
        Add("POST", "/feature-flags/{}/restore", editor, write);
        Add("GET", "/feature-flags/{}/rollout-schedule", viewer, read);
        Add("POST", "/feature-flags/{}/rollout-schedule", editor, write);
        Add("DELETE", "/feature-flags/{}/rollout-schedule", editor, write);
        Add("PUT", "/feature-flags/{}", editor, write);
        Add("DELETE", "/feature-flags/{}", editor, write);
        Add("GET", "/annotations", viewer, read);
        Add("POST", "/annotations", editor, write);
        Add("PUT", "/annotations/{}", editor, write);
        Add("DELETE", "/annotations/{}", editor, write);
        Add("GET", "/event-definitions", viewer, analytics);
        Add("GET", "/property-definitions", viewer, analytics);
        Add("GET", "/ingestion/metrics", viewer, analytics);
        Add("POST", "/capture/validate", viewer, analytics);
        Add("GET", "/capture-receipts/{}", viewer, analytics);
        Add("GET", "/ingestion/limits", viewer, analytics);
        Add("PUT", "/ingestion/limits", admin, null);
        Add("GET", "/ingestion/dead-letters/{}/replay-check", viewer, analytics);
        Add("POST", "/ingestion/dead-letters/replay-batch", admin, null);
        Add("GET", "/ingestion/dead-letters", viewer, analytics);
        Add("GET", "/ingestion/dead-letters/{}", viewer, analytics);
        Add("POST", "/ingestion/dead-letters/{}/replay", admin, null);
        Add("GET", "/export/events", viewer, analytics);
        Add("GET", "/export/persons", viewer, analytics);
        Add("GET", "/export/insights/{}", viewer, analytics);
        Add("POST", "/exports", editor, exports);
        Add("POST", "/exports/{}/retry", editor, exports);
        Add("POST", "/exports/{}/cancel", editor, exports);
        Add("GET", "/exports", viewer, analytics);
        Add("DELETE", "/exports/{}", editor, exports);
        Add("GET", "/exports/{}/integrity", viewer, analytics);
        Add("GET", "/exports/{}", viewer, analytics);
        Add("GET", "/exports/{}/download", viewer, analytics);
        return rules;
    }
}
