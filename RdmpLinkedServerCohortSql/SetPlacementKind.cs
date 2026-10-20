// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

namespace RdmpLinkedServerCohortSql;

/// <summary>Where a set runs.</summary>
internal enum SetPlacementKind
{
    /// <summary>The set and its patient index table, if any, are on the home server.</summary>
    Home,
    /// <summary>The set is on a linked server, with its patient index table if it has one.</summary>
    RemoteWhole,
    /// <summary>The set is on the home server, but its patient index table is on a linked server.</summary>
    HomeWithRemotePit
}
