using System;

namespace ToolboxMatrix;

// Compiled capability, never a persisted preference. Both registries and their
// navigation/rendering code remain in every build; only V1 presentation is gated.
internal static class ReleaseFeatures
{
#if TOOLBOX_V1
    internal static bool IncludeUnfinishedCollections => false;
    internal static string VersionSuffix => " candidate";
#else
    internal static bool IncludeUnfinishedCollections => true;
    internal static string VersionSuffix => "";
#endif

    internal static bool IsCollectionVisible(string key) =>
        IncludeUnfinishedCollections ||
        !(key.Equals("tools", StringComparison.OrdinalIgnoreCase) ||
          key.Equals("sites", StringComparison.OrdinalIgnoreCase));

    internal static string ResolveRoute(string route) =>
        route.StartsWith("collection:", StringComparison.OrdinalIgnoreCase) &&
        !IsCollectionVisible(route[11..]) ? "collections" : route;

    internal static string CollectionsFooter => IncludeUnfinishedCollections
        ? "Open a collection. Application collections verify their entries against WinGet; Tools and Sites are curated separately."
        : "Open a collection. Application collections verify their entries against WinGet.";
}
