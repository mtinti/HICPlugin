// A build script refers to an existing catalogue, table, project or published filter by id, followed by its name.
// The id is the reference: everyone who uses a script works on the same platform database, so ids are stable.
// The name lets a person read the script, and catches a wrong id. A typed command may pass null to skip the check.

using System;
using Rdmp.Core.MapsDirectlyToDatabaseTable;

namespace RdmpCohortScript;

/// <summary>Checks that an existing object still has the name a build script gives it.</summary>
internal static class ExistingObjectName
{
    /// <summary>
    /// Returns null when the name matches, or when <paramref name="expectedName"/> is null. Otherwise returns a
    /// message that names the object, its current name, the name in the script, and what to do.
    /// </summary>
    public static string Mismatch(string kind, IMapsDirectlyToDatabaseTable existing, string currentName,
        string expectedName)
    {
        if (expectedName == null || string.Equals(currentName, expectedName, StringComparison.Ordinal))
            return null;

        return $"The script names {kind} {existing.ID} \"{expectedName}\", but {kind} {existing.ID} is called " +
               $"\"{currentName}\". Check that the id is right. If the {kind} was renamed, correct the name in the " +
               "script, or give null instead of the name to skip this check.";
    }
}
