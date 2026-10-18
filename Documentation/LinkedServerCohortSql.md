# Linked-server cohort SQL

Command: `ViewCohortLinkedServerSql` (project `Rdmp.LinkedServerCohortSql`).

Writes runnable T-SQL for a cohort identification configuration whose sets live on different SQL Servers
that are reachable from the home server as linked servers. Core's "Query Builder SQL/Results (No Cache)"
view writes one statement that names tables on every server; that statement cannot run when the sets are
on different servers. This command wraps each far-side block in `OPENQUERY` so the script runs on the home
server.

It only writes text. It reads the RDMP catalogue, opens no data connection, reads no stored credential and
executes nothing. Whoever runs the script does so in their own SQL tool under their own login.

## Two shapes

- **Staged** (the one to run). Each far-side set, and each far-side patient index table used by home sets,
  is copied into a temp table first, so each link is used for seconds. Each home set then runs as its own
  statement into a temp table, and the set arithmetic runs last over the temp tables, as RDMP's cached
  build does. The script returns a count grid (one row per set in cohort tree order, with the set's ID and
  temp table, then a "Final cohort" total) and then the cohort, which is also kept in `#rdmp_cohort`.
- **Composite** (for reading). One statement: core's no-cache SQL with the remote parts in `OPENQUERY`.
  SQL Server plans it as one query, so a cohort with heavy sets can take hours where the cached build
  takes minutes; the header says not to run it on production.

Temp tables are named `#rdmp_s<set ID>`, `#rdmp_r<set ID>`, `#rdmp_pit<patient index ID>` and
`#rdmp_cohort`, and are kept after the run for inspection. To re-run after changing the cohort, close the
query window or run the commented DROP block at the end of the script.

## Remote blocks

Every far-side block uses `EXEC('USE [database] ...') WITH RESULT SETS (...)`, so it does not depend on the
link login's default database. The declared columns are the columns RDMP selects for that set, in RDMP's
order, typed from the catalogue (synchronise remote table metadata first). When a column's type or RDMP's
column order cannot be derived, the block falls back to a guarded form that fails with Msg 50000 unless the
link login's default database is the right one; the header then carries a warning.

The `OPENQUERY` blocks run on the far server as the login the linked server maps the caller's login to
(`sys.linked_logins`). The header names each link.

## GUI and CLI

Right-clicking a cohort identification configuration, or a project's association to one, shows a
**Linked Servers SQL** submenu with four entries: the staged script, the staged script saved to a file, the
composite, and the composite saved to a file. Scripts over 20,000 characters go to the save dialog.

```
rdmp cmd ViewCohortLinkedServerSql CohortIdentificationConfiguration:<id> out.sql true   # staged, to a file
rdmp cmd ViewCohortLinkedServerSql CohortIdentificationConfiguration:<id> null true      # staged, to stdout
rdmp cmd ViewCohortLinkedServerSql CohortIdentificationConfiguration:<id> out.sql        # composite, to a file
```

A file argument named `true` or `false` is refused with a hint, because `<id> true` would otherwise write the
composite to a file named `true`.

## Limits and refusals

- Home and far servers must be Microsoft SQL Server; other DBMSs are refused by `DatabaseType`.
- Refused with a reason naming the set: a set or patient index spanning servers or databases, nested patient
  index tables, more than one patient index per set, API sets, a far-side batch over the `OPENQUERY` literal
  limit.
- The staged shape also refuses a home column whose collation a temp table could change
  (`StagedCollationUnknown`), an undetermined column order (`StagedColumnOrderAmbiguous`) and unnamed or
  case-clashing columns (`ExpressionIdentifierNeedsAlias`). The composite still renders these.
- Sets with TOP X carry a header warning: RDMP writes TOP X without ORDER BY, so their membership is not
  deterministic.

## Tests

`HICPluginTests/LinkedServerCohortSql/`. In-memory tests cover routing, header, both shapes, the staged
refusals and the command surface. `LoopbackDatabaseTests` create test databases and a loopback linked
server, execute both shapes and compare them with core's SQL and hand-calculated results; they are
Inconclusive when the test login cannot create a linked server (set `RDMP_LINKED_REQUIRE_LINK=1` to make
that a failure).
