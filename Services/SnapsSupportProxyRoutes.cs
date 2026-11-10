using System;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Veiculando.WhiteLabel.Api.Services;

internal static class SnapsSupportProxyRoutes
{
    public const string Prefix = "/api/wl/snaps";
    private const string ProjectPrefix = "/public/projects/53896f31-9d7f-458b-ab63-8bd6ddb549f6";
    private const string Uuid = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";
    private static readonly Regex Card = new(@"\A/support/cards/" + Uuid + @"\z", RegexOptions.CultureInvariant);
    private static readonly Regex Status = new(@"\A/support/cards/" + Uuid + @"/status\z", RegexOptions.CultureInvariant);
    private static readonly Regex Chat = new(@"\A/support/chats/" + Uuid + @"/(messages|proposal/repository|confirm|close)\z",
        RegexOptions.CultureInvariant);
    private static readonly string[] TargetParameters = { "host", "url", "target", "project_id", "projectId", "redirect" };

    public static bool IsProxy(PathString path) => path.StartsWithSegments(Prefix, StringComparison.Ordinal);

    public static bool Allows(string method, string route, string rawPath, IQueryCollection query, out bool chatSession)
    {
        chatSession = false;
        if (route.Length > 512 || !route.StartsWith(ProjectPrefix + "/", StringComparison.Ordinal)
            || !string.Equals(rawPath, Prefix + route, StringComparison.Ordinal)
            || query.Keys.Any(key => TargetParameters.Contains(key, StringComparer.OrdinalIgnoreCase)))
            return false;

        var suffix = route[ProjectPrefix.Length..];
        chatSession = method == "POST" && Chat.IsMatch(suffix);
        return method switch
        {
            "GET" => suffix == "/support" || Card.IsMatch(suffix),
            "POST" => suffix is "/cards" or "/support/upload" or "/support/chats" || chatSession,
            "PATCH" => Card.IsMatch(suffix) || Status.IsMatch(suffix),
            _ => false
        };
    }
}
