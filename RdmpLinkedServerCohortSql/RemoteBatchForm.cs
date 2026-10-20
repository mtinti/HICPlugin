// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

namespace RdmpLinkedServerCohortSql;

/// <summary>How the query on a linked server reaches the right database.</summary>
public enum RemoteBatchForm
{
    /// <summary>The checked form: the query runs in the link login's default database, after checking that it is the right
    /// one. It stops with an error if not. The plugin uses it only when it cannot describe the set's columns.</summary>
    Guarded,
    /// <summary>The USE form: the query runs <c>EXEC('USE [db]; ...') WITH RESULT SETS (...)</c>, so it switches to the right
    /// database itself, and declares every column it returns.</summary>
    UseDatabase
}
