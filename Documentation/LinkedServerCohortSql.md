# Linked-server cohort SQL

Command: `ViewCohortLinkedServerSql` (project `RdmpLinkedServerCohortSql`).

This command writes T-SQL that runs on one server even when the cohort's sets live on several SQL Servers.
RDMP's own "Query Builder SQL/Results (No Cache)" view writes one statement that names tables on every server,
and that statement fails when the sets are on different servers. This command reads each remote set through
`OPENQUERY` over a linked server, so the whole script runs on the home server.

The command only writes text. It reads the RDMP catalogue, opens no data connection, reads no stored
credential and runs nothing. Whoever runs the script does so in their own SQL tool, under their own login.

## Words used here

- **Home server**: the server the script runs on. The script's first lines name it.
- **Linked server**: another SQL Server that the home server reads from with `OPENQUERY`.
- **Patient index table**: a set that other sets join to.
- **Temporary table (temp table)**: a table whose name starts with `#`. SQL Server keeps it only for the query
  window that made it.
- **Collation**: the rules SQL Server uses to compare and sort text, for example whether `'a'` equals `'A'`.
- **Alias**: a name given to a column, written after `AS`, for example `COUNT(*) AS n`.
- **Metadata**: what RDMP's catalogue records about a table or column: its server, database, type and collation.

## Two scripts

- **Staged, to run.** The script first copies each set and patient index table that lives on a linked server
  into a temp table, so each linked server is busy for seconds only. Next it runs each set on the home server
  as its own statement into a temp table, as RDMP's cached build does. Last it combines the temp tables with
  UNION, INTERSECT and EXCEPT, as the cohort tree says. It returns a count table (one row per set, in the
  order the cohort tree combines them, with the set's ID and temp table, then a "Final cohort" row), and then
  the cohort, which it also keeps in `#rdmp_cohort`.
- **For inspection only.** One statement: RDMP's own no-cache SQL with the remote parts in `OPENQUERY`. SQL
  Server plans it as one query, so a cohort with heavy sets can run for hours where the cached build takes
  minutes. The header says not to run it on production. (In the code it is called the composite.)

The staged script names its temp tables as follows: `#rdmp_s<set ID>` holds a set from the home server,
`#rdmp_r<set ID>` a set copied from a linked server, `#rdmp_pit<patient index ID>` a patient index table copied
from a linked server, and `#rdmp_cohort` the final cohort. It keeps them after the run, so you can inspect
each set. To run the script again after you change the cohort, close the query window first, or run the
DROP TABLE lines at the end of the script (remove the `--` before each).

## Queries on linked servers

Each query on a linked server uses the **USE form**: `EXEC('USE [database] ...') WITH RESULT SETS (...)`. It
switches to the right database itself, so it works whatever the link login's default database is. It declares
the columns RDMP selects for that set, in RDMP's order, with types from the catalogue. Synchronise each remote
table in RDMP first, so the catalogue has its current column types.

When the plugin cannot describe a set's columns, it uses the **checked form** instead, and the header and a
warning give the reason: a column type it cannot work out, a column collation the catalogue does not record, a
count without an alias, or a column order that is not certain. The checked form runs in the link login's
default database, after checking that it is the right one, and stops with an error if it is not. Fixing the
reason brings back the USE form.

Each `OPENQUERY` block runs on the linked server under the login that the linked server maps the caller's
login to (`sys.linked_logins`). That can be a shared login. The header names each linked server.

## Menu and command line

Right-clicking a cohort identification configuration, or a project's association to one, shows a
**Linked Servers SQL** submenu with four entries: "Query Builder SQL (Linked Servers, staged, to run)" and
"Query Builder SQL (Linked Servers, for inspection only)", each also with "save to file...". A script longer
than 20,000 characters goes to the save dialog.

The command takes three arguments in this order: the cohort, the output file (or `null`), and `true` for the
staged script. These forms were checked on RDMP 9.3.0 (`rdmp ViewCohortLinkedServerSql ...` without `cmd` also
works):

```
rdmp cmd ViewCohortLinkedServerSql CohortIdentificationConfiguration:<id> out.sql true   # staged script, to a file
rdmp cmd ViewCohortLinkedServerSql CohortIdentificationConfiguration:<id> null true      # staged script, printed
rdmp cmd ViewCohortLinkedServerSql CohortIdentificationConfiguration:<id> out.sql        # script for inspection, to a file
```

The command refuses a file named `true` or `false` and shows the correct arguments. Without this check,
`<id> true` would save the script for inspection to a file named `true`.

## Limits and refusals

Every refusal names the set or table, says why, and says what to do. The command refuses a cohort when:

- the home server or a linked server is not Microsoft SQL Server (it checks each table's `DatabaseType`);
- RDMP records no server or no database for a table;
- the tables of one set are on different servers, or a set on a linked server and its patient index table are
  on different servers;
- a set on a linked server, with its patient index table, reads from more than one database. The plugin sends
  one query per set to a linked server, and that query uses one database. This is a limit of the plugin, not
  of SQL Server;
- a set joins more than one patient index table, or a patient index table uses another one;
- a set reads from an API instead of a table;
- the query for a linked server is longer than `OPENQUERY` accepts (8,000 characters, or 4,000 with non-ASCII
  text).

The staged script also refuses three cases, which the script for inspection still writes:

- A text column could compare differently in a temp table (`StagedCollationUnknown`), for example a number cast
  to text or a CASE. Add `COLLATE <collation name>` at the end of the column's SQL. A plain column, or MIN or
  MAX of one, is never refused. If the expression already has a COLLATE inside it, a COLLATE at the end does
  not help, and moving it can change the result.
- RDMP's column order is not certain (`StagedColumnOrderAmbiguous`): two columns have the same Order, or two
  Orders are so far apart that RDMP's comparison overflows. Give each column a different, small Order.
- A column has no name, or two names differ only by case (`ExpressionIdentifierNeedsAlias`). Give the column
  an alias.

A set with TOP X gets a warning: RDMP writes TOP X without ORDER BY, so SQL Server may keep different rows on
each run.

## Tests

The tests are in `HICPluginTests/LinkedServerCohortSql/`. Tests without a database check where each set runs,
the header, both scripts, the staged refusals, and the menu and command-line arguments. Each message that tells
the user to do something has a test that does it and checks that the cohort then builds.
`LoopbackDatabaseTests` create test databases and a loopback linked server (one that points back to the same
server), run both scripts, and compare them with RDMP's query builder and with hand-calculated results. They
are Inconclusive when the test login cannot create a linked server, for example on LocalDB; set
`RDMP_LINKED_REQUIRE_LINK=1` to make that a failure.
