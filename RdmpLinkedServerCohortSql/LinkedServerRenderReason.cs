// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

namespace RdmpLinkedServerCohortSql;

public enum LinkedServerRenderReason
{
    UnsupportedHomeDatabaseType,
    UnsupportedRemoteDatabaseType,
    RemoteResultTypeUnknown,
    NestedPatientIndexTable,
    SetSpansServers,
    JoinSpansServers,
    MultiplePatientIndexTables,
    MultipleRemoteDatabases,
    BatchTooLong,
    ApiSetRequiresCache,
    EmptyContainer,
    ExpressionIdentifierNeedsAlias,
    ReservedParameterName,
    HomeDatabaseUnknown,
    StagedCollationUnknown,
    StagedColumnOrderAmbiguous
}
