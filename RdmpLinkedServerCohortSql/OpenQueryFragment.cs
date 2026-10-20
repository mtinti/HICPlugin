// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

namespace RdmpLinkedServerCohortSql;

/// <summary>The OPENQUERY call for one set on a linked server, and any warnings about it.</summary>
internal sealed record OpenQueryFragment(string Sql, IReadOnlyList<string> Warnings);
