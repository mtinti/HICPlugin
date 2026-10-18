// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using FAnsi;
using FAnsi.Discovery.QuerySyntax;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.QueryBuilding;

namespace Rdmp.LinkedServerCohortSql;

internal static class SqlServerOpenQueryWrapper
{
    internal const string ReservedParameter = "@rdmp_linked_db";

    internal static string QuoteIdentifier(string name, IQuerySyntaxHelper syntax) =>
        syntax.OpenQualifier + name.Replace(syntax.CloseQualifier, syntax.CloseQualifier + syntax.CloseQualifier) + syntax.CloseQualifier;

    internal static string UseDatabase(string database, IQuerySyntaxHelper syntax) => "USE " + QuoteIdentifier(database, syntax) + ";";

    internal static string DescribeColumn(ResultColumn column, IQuerySyntaxHelper syntax) =>
        QuoteIdentifier(column.Name, syntax) + " " + column.SqlType +
        (column.Collation == null ? "" : " COLLATE " + column.Collation);

    public static OpenQueryFragment Wrap(CohortQueryBuilderDependencySql block, ITableInfo remoteTable,
        RemoteBatchForm form, IReadOnlyList<ResultColumn> resultColumns)
    {
        if (remoteTable.DatabaseType != DatabaseType.MicrosoftSQLServer)
            throw new LinkedServerRenderException(LinkedServerRenderReason.UnsupportedRemoteDatabaseType,
                $"Table {remoteTable} on {remoteTable.Server} uses {remoteTable.DatabaseType}; use SQL Server tables.");
        if (string.IsNullOrWhiteSpace(remoteTable.Server))
            throw new LinkedServerRenderException(LinkedServerRenderReason.SetSpansServers,
                $"Table {remoteTable} has no server; specify its server in the catalogue.");
        if (string.IsNullOrWhiteSpace(remoteTable.Database))
            throw new LinkedServerRenderException(LinkedServerRenderReason.MultipleRemoteDatabases,
                $"Table {remoteTable} on {remoteTable.Server} has no database; synchronise its catalogue metadata.");

        var syntax = remoteTable.GetQuerySyntaxHelper();
        var database = remoteTable.GetDatabaseRuntimeName();
        var server = remoteTable.Server.Trim();
        var warnings = new List<string>();
        var parameters = block.ParametersUsed.GetFinalResolvedParametersList().ToArray();
        if (parameters.Any(p => string.Equals(p.ParameterName, ReservedParameter, StringComparison.OrdinalIgnoreCase)))
            throw new LinkedServerRenderException(LinkedServerRenderReason.ReservedParameterName,
                $"Table {remoteTable} on {server}: rename parameter {ReservedParameter}; it is reserved for the database guard.");
        var declarations = QueryBuilder.GetParameterDeclarationSQL(SqlParameterDependencies.Resolve(block.Sql, parameters, warnings));
        var nl = Environment.NewLine;
        var batch = "SET NOCOUNT ON;" + nl + declarations + block.Sql;
        string argument;
        switch (form)
        {
            case RemoteBatchForm.Guarded:
                argument = "SET NOCOUNT ON;" + nl +
                    $"DECLARE {ReservedParameter} sysname = DB_NAME();" + nl +
                    $"IF {ReservedParameter} <> N'{Escape(database)}' " +
                    "RAISERROR('RDMP linked-server cohort: link login default database through link %s is %s, expected %s; set the linked-server login default database (sp_addlinkedsrvlogin / ALTER LOGIN) or qualify objects three-part', 16, 1, " +
                    $"N'{Escape(server)}', {ReservedParameter}, N'{Escape(database)}');" + nl + declarations + block.Sql;
                break;
            case RemoteBatchForm.UseDatabase:
                if (resultColumns == null || resultColumns.Count == 0)
                    throw new LinkedServerRenderException(LinkedServerRenderReason.RemoteResultTypeUnknown,
                        $"Table {remoteTable} on {server}: the EXEC form needs named, typed result columns; synchronise metadata or alias expressions with an explicit CAST.");
                argument = "EXEC('" + Escape(UseDatabase(database, syntax) + nl + batch) + "') WITH RESULT SETS ((" +
                    string.Join(", ", resultColumns.Select(c => DescribeColumn(c, syntax))) + "));";
                break;
            default: throw new ArgumentOutOfRangeException(nameof(form));
        }

        var unicode = argument.Any(c => c > 127);
        var limit = unicode ? 4000 : 8000;
        // SQL Server limits the decoded OPENQUERY argument, not the escaped source literal.
        if (argument.Length > limit)
            throw new LinkedServerRenderException(LinkedServerRenderReason.BatchTooLong,
                $"Table {remoteTable} on {server}: remote batch is {argument.Length} characters; limit {limit}" +
                (unicode ? " (non-ASCII text requires a Unicode literal)." : " (ASCII literal).") +
                " Shorten the filter or use the core cached cohort build.");
        return new OpenQueryFragment($"OPENQUERY({QuoteIdentifier(server, syntax)}, {(unicode ? "N" : "")}'{Escape(argument)}')",
            warnings.AsReadOnly());
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
