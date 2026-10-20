// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

namespace RdmpLinkedServerCohortSql;

/// <summary>Why the plugin could not write SQL for a cohort. New values go at the end, so existing values keep their numbers.</summary>
public enum LinkedServerRenderReason
{
    /// <summary>The home server, where the script runs, is not Microsoft SQL Server.</summary>
    UnsupportedHomeDatabaseType,
    /// <summary>A table on a linked server is not on Microsoft SQL Server.</summary>
    UnsupportedRemoteDatabaseType,
    /// <summary>The plugin cannot work out the name, type, collation or order of a column that a linked server returns.</summary>
    RemoteResultTypeUnknown,
    /// <summary>A patient index table uses another patient index table.</summary>
    NestedPatientIndexTable,
    /// <summary>The tables of one set are on different servers.</summary>
    SetSpansServers,
    /// <summary>A set on a linked server and its patient index table are on different servers.</summary>
    JoinSpansServers,
    /// <summary>A set joins more than one patient index table.</summary>
    MultiplePatientIndexTables,
    /// <summary>A set on a linked server, with its patient index table, reads from more than one database.</summary>
    MultipleRemoteDatabases,
    /// <summary>The query sent to a linked server is longer than OPENQUERY accepts.</summary>
    BatchTooLong,
    /// <summary>A set reads from an API, which only RDMP's cached build can run.</summary>
    ApiSetRequiresCache,
    /// <summary>The cohort, or one of its containers, has no enabled set with a table.</summary>
    EmptyContainer,
    /// <summary>A column has no name, or two columns have the same name.</summary>
    ExpressionIdentifierNeedsAlias,
    /// <summary>A parameter uses the name the plugin keeps for its own database check.</summary>
    ReservedParameterName,
    /// <summary>RDMP does not record the database of a table on the home server.</summary>
    HomeDatabaseUnknown,
    /// <summary>The staged script cannot copy a column into a temp table without changing how its text compares.</summary>
    StagedCollationUnknown,
    /// <summary>The staged script needs the exact column order of a set, and RDMP's order is not certain.</summary>
    StagedColumnOrderAmbiguous,
    /// <summary>RDMP does not record a server for a table.</summary>
    ServerUnknown,
    /// <summary>RDMP does not record the database of a table on a linked server.</summary>
    RemoteDatabaseUnknown,
    /// <summary>Two parameters have names that differ only by upper and lower case.</summary>
    ParameterNamesClash
}
