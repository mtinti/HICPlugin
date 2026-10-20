// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

namespace RdmpLinkedServerCohortSql;

/// <summary>Which script the plugin writes.</summary>
public enum CohortSqlShape
{
    /// <summary>The script for inspection only: one statement, RDMP's own SQL with the linked-server parts in OPENQUERY.</summary>
    Composite,
    /// <summary>The staged script, the one to run: each set goes into its own temp table, and the tables are combined at the end.</summary>
    Staged
}
