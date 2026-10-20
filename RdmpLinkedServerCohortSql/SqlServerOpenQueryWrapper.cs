// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using FAnsi;
using FAnsi.Discovery.QuerySyntax;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.QueryBuilding;

namespace RdmpLinkedServerCohortSql;

/// <summary>
/// Wraps the SQL of a set that lives on a linked server in OPENQUERY, so the home server can run it. The query on the linked
/// server takes one of two forms (<see cref="RemoteBatchForm"/>): the USE form, which switches to the right database itself,
/// or the checked form, which runs in the link login's default database after checking that it is the right one.
/// </summary>
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
        // The builder checks these first; the wrapper repeats the checks because it can also be called on its own.
        if (remoteTable.DatabaseType != DatabaseType.MicrosoftSQLServer)
            throw new LinkedServerRenderException(LinkedServerRenderReason.UnsupportedRemoteDatabaseType,
                $"Table {remoteTable} is on server {remoteTable.Server}, which is a {remoteTable.DatabaseType} server. This plugin reads only from Microsoft SQL Server.");
        if (string.IsNullOrWhiteSpace(remoteTable.Server))
            throw new LinkedServerRenderException(LinkedServerRenderReason.ServerUnknown,
                $"RDMP does not record a server for table {remoteTable}.", "Set the table's server in RDMP.");
        if (string.IsNullOrWhiteSpace(remoteTable.Database))
            throw new LinkedServerRenderException(LinkedServerRenderReason.RemoteDatabaseUnknown,
                $"RDMP does not record which database table {remoteTable} on server {remoteTable.Server} is in.",
                "Synchronise the table in RDMP so its database is recorded.");

        var syntax = remoteTable.GetQuerySyntaxHelper();
        var database = remoteTable.GetDatabaseRuntimeName();
        var server = remoteTable.Server.Trim();
        var warnings = new List<string>();
        var parameters = block.ParametersUsed.GetFinalResolvedParametersList().ToArray();
        if (parameters.Any(p => string.Equals(p.ParameterName, ReservedParameter, StringComparison.OrdinalIgnoreCase)))
            throw new LinkedServerRenderException(LinkedServerRenderReason.ReservedParameterName,
                $"The plugin uses the name {ReservedParameter} for its own database check.", "Rename your parameter.");
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
                    "RAISERROR('RDMP linked-server cohort: the login that linked server %s uses has default database %s, but this query needs " +
                    "database %s. Ask the DBA to change the default database of that login (ALTER LOGIN ... WITH DEFAULT_DATABASE on the linked server).', 16, 1, " +
                    $"N'{Escape(server)}', {ReservedParameter}, N'{Escape(database)}');" + nl + declarations + block.Sql;
                break;
            case RemoteBatchForm.UseDatabase:
                if (resultColumns == null || resultColumns.Count == 0)
                    throw new LinkedServerRenderException(LinkedServerRenderReason.RemoteResultTypeUnknown,
                        $"The USE form needs a name and a type for each column that linked server {server} returns, and none was given.",
                        "Synchronise the table in RDMP, or write each expression as CAST(... AS type) AS name.");
                argument = "EXEC('" + Escape(UseDatabase(database, syntax) + nl + batch) + "') WITH RESULT SETS ((" +
                    string.Join(", ", resultColumns.Select(c => DescribeColumn(c, syntax))) + "));";
                break;
            default: throw new ArgumentOutOfRangeException(nameof(form));
        }

        var unicode = argument.Any(c => c > 127);
        var limit = unicode ? 4000 : 8000;
        // SQL Server limits the length of the query text that OPENQUERY receives, after its doubled quotes are read as one.
        if (argument.Length > limit)
            throw new LinkedServerRenderException(LinkedServerRenderReason.BatchTooLong,
                $"The query sent to linked server {server} is {argument.Length} characters long, but OPENQUERY accepts at most {limit}" +
                (unicode ? " when the query contains non-ASCII text." : "."),
                "Shorten the set's filters, or build this cohort with RDMP's normal cached build.");
        return new OpenQueryFragment($"OPENQUERY({QuoteIdentifier(server, syntax)}, {(unicode ? "N" : "")}'{Escape(argument)}')",
            warnings.AsReadOnly());
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
