// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

namespace Rdmp.LinkedServerCohortSql;

public sealed class LinkedServerRenderException(LinkedServerRenderReason reason, string detail)
    : Exception($"{reason}: {detail}")
{
    public LinkedServerRenderReason Reason { get; } = reason;
}
