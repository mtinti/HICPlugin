// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

namespace RdmpLinkedServerCohortSql;

/// <summary>One column that a query on a linked server returns: its name, SQL type and, for text, its collation.</summary>
internal sealed record ResultColumn(string Name, string SqlType, string Collation = null);
