# RdmpCohortScript

RdmpCohortScript writes a cohort as a build script, and builds a new cohort from one. A build script is a text
file of RDMP commands, one per line, that a person can read and an agent can write. People and agents therefore
use the same commands, and a cohort can be rebuilt, compared or changed through its script.

The plugin reads and writes RDMP metadata only. It runs no cohort query, opens no data table and counts no
patients.

## Words used here

| Use | Meaning | Replace |
|---|---|---|
| cohort | A cohort identification configuration: the tree of sets and containers that selects patients. | CIC (allowed after it is spelled out once) |
| set | One cohort set: a query on one catalogue that returns patient identifiers. | aggregate, leaf |
| container | A node of the cohort tree that combines its children with UNION, INTERSECT or EXCEPT. | |
| patient index table | A set that other sets join, for example to compare dates. | PIT (allowed after it is spelled out once), joinable |
| filter container | A node of a set's filter tree that combines its filters with AND or OR. | |
| published filter | A filter saved on a catalogue, which a set can copy. | extraction filter (in text for users) |
| mandatory filter | A published filter that RDMP copies into every new set on its catalogue. | |
| build script | A `build.script.yaml` file: the commands that build one cohort, in order. | script file, recipe |
| handle | A name such as `$a2` that a line gives to an object it makes, so later lines can refer to it. | variable |
| existing object | A catalogue, table, project or published filter that the script uses but does not make. The script names it by id and name. | |
| catalogue manifest | A `catalogue-manifest.yaml` file: the catalogues, tables and projects a script may refer to, with ids and names. | |
| requirement | A `requirement.md` file: the request the cohort answers, written by hand. | |
| authored script | A build script that a person or an agent wrote, rather than one exported from a cohort. | |

## Commands

| Command | What it does |
|---|---|
| `ExportCohortAsScript` | Writes a cohort's three files: `requirement.md` (only if missing), `build.script.yaml` and `catalogue-manifest.yaml`. |
| `ExportCohortsAsScripts` | Exports many cohorts, each into the folder a list file gives it. |
| `ExportCatalogueManifest` | Writes the manifest for a cohort, for chosen catalogues, or for every catalogue a cohort can use. |
| `CheckCohortScript` | Checks a build script and builds nothing. |
| `BuildCohortFromScript` | Checks a build script, then builds a new cohort from it. |
| `DescribeCohortScriptCommands` | Writes the reference of the commands a build script may use. |

Each line of a build script is one of 18 commands: 13 from this plugin, 5 from RDMP. `COMMANDS.md` beside this
file is the reference, with one example per command. It is the output of `DescribeCohortScriptCommands`, and a
test fails when the two differ.

## Build

The plugin builds with the rest of HICPlugin. It references the RDMP submodule as a project, and the submodule
is pinned at RDMP 9.3.0. RDMP turns warnings into errors, and some of its packages carry security advisories
(NU1902 to NU1904), so the build keeps those as warnings:

    dotnet build RdmpCohortScript/RdmpCohortScript.csproj -p:WarningsNotAsErrors='"NU1902;NU1903;NU1904"'

`Plugin/main/main.csproj` includes it, so the HICPlugin package carries it.

## Install

Install the HICPlugin package as usual. In the desktop client, right-click a cohort: the **Cohort script**
submenu has the entries for export, the manifest, the rebuild and the check. On the command line, every command
runs as `rdmp cmd <Command> ...`.

## Use

1. Export a cohort:

       rdmp cmd ExportCohortAsScript CohortIdentificationConfiguration:42 .\out

   The files go into `.\out\<cohort name>\`. The export replaces an earlier build script and manifest, and keeps
   a requirement written by hand. In the desktop client it asks before it replaces anything.

2. Export many cohorts. Write a list file with one cohort per line, the id and then the folder. A relative folder
   is read from the list file's folder, and a line that starts with # is a comment:

       # cohort id, then its folder
       42  diabetes-since-2010
       57  heart failure

       rdmp cmd ExportCohortsAsScripts .\cohorts.txt

   Each cohort goes to its listed folder, also after it was renamed. The command writes nothing if the list names
   an unknown cohort, a cohort twice, two cohorts for one folder (also through a link to it), or a cohort the
   format cannot carry.

3. Check a script. Give `true` as the second argument for an authored script, and `true` as the third to accept
   existing objects renamed since the export, as the rebuild can:

       rdmp cmd CheckCohortScript .\out\diabetes-since-2010\build.script.yaml
       rdmp cmd CheckCohortScript .\drafts\new-cohort.build.script.yaml true
       rdmp cmd CheckCohortScript .\out\diabetes-since-2010\build.script.yaml false true

   The check lists every problem with its line in the file, and ends with an error when there is one. It passes
   exactly when the rebuild, with the same option for renamed objects, would start building.

4. Build a new cohort from a script:

       rdmp cmd BuildCohortFromScript .\out\diabetes-since-2010\build.script.yaml "Diabetes since 2010 (rebuilt)"

   The new name must be free. A third argument, `true`, accepts existing objects renamed since the export, with
   a warning for each.

5. Write a manifest for an author:

       rdmp cmd ExportCatalogueManifest null null .\out\all-catalogues.yaml
       rdmp cmd ExportCatalogueManifest null Catalogue:717,718 .\out\two-catalogues.yaml

## How a rebuild runs

- The rebuild reads and checks the whole script before it builds anything. A script with a problem builds
  nothing.
- It runs each line through the command the line names, with the values the line gives. The values never pass
  through RDMP's command-line reader, which would read the word null as no value, or `Catalogue:12` in a filter
  name as a catalogue.
- After each line it checks that the line made its change.
- If a line fails, the rebuild stops, names the line, and removes the partly built cohort and everything the run
  saved. It removes nothing else, so other people's cohorts on the same platform are safe, also one with the
  same name.
- In the desktop client, the rebuild does not ask about the containers it deletes, and restores that setting
  afterwards.

## Mandatory filters

An exported script copies no mandatory filters (the last argument of each set line is `false`). The cohort
already holds the filters as they were when it was built, and the script writes those. If a mandatory filter
changed since, copying it again would apply both versions and lose patients.

A new cohort normally needs the current mandatory filters. An authored script writes `true` instead, and
leaves out the filters the export copied (each has a `SetFilterSource` line that names a filter the manifest
marks as mandatory). `CheckCohortScript` with `true` warns about each set line that leaves them out.

When RDMP copies mandatory filters, it puts them in a new top filter container of the set. A set has one top
filter container, so after a line that writes `true` the script uses `UseTopFilterContainer` instead of
`AddNewFilterContainer`. It binds the container the set has, or makes one when the catalogue has no mandatory
filters, and the set's own filters go beside the mandatory ones. The check refuses `AddNewFilterContainer` on a
set that already has a top filter container.

## Limits and refusals

The export refuses, with a message and no files, a cohort that uses something the format does not carry:

- a cohort without a root container
- a set on a catalogue served by an API, which has no identifier column
- a set that selects columns other than its identifier column, or has a pivot or a date axis
- a set with no identifier column or more than one, or with a column no longer in its catalogue
- a patient index table that uses one catalogue column twice
- a column name with a space, a quote, a dot or a brace, which cannot be part of a handle, and an extra column of
  a patient index table with a comma in its name, which cannot be listed
- SQL that already contains the text `{$`, which the script uses for its markers
- a set whose name still starts with `cic_` after RDMP's prefix is removed

The check and the rebuild refuse a script that breaks a rule of the format, for example a handle used before
a line makes it, an existing object named with a different name, or a filter that uses a parameter no line
declares where RDMP gives it to the filter (the cohort, the filter's set or any filter of that set, including
the mandatory filters a set line copies, or a patient index table the set joins). Each refusal has a reason
code, `CohortScriptRefusal`, which tests and calling code use instead of the message.

The check also warns, without refusing, about every `SetTopX` line. RDMP 9.3.0 builds a cohort set's Top X
without an ORDER BY, so the patients it keeps are not fixed, and a rebuild may return others. The column to
order by is kept for the set's graph and does not change this.

A script without the line `cohort-script-format: "1"` was written by an earlier version of the export, in
another format. The rebuild does not read it. Export that cohort again.

## Tests

The tests are in `HICPluginTests/CohortScript`:

- unit tests, in memory, for the format, the writers, the check, the rebuild, the clean-up and the commands
- database tests, which build cohorts on a test SQL Server, run the original and the rebuilt cohort, and
  compare their patients
- probes of the RDMP 9.3.0 behaviours the plugin relies on, so a change in RDMP shows up as a failing test
- the example scripts in `HICPluginTests/CohortScript/Examples`, which are checked, rebuilt and exported again.

The database tests use the server in `TestDatabases.txt`.

## Notes for developers

- `CohortScriptFormat` holds the command table. The check, the rebuild and the reference all read it. A new
  command needs a row there, a class, an example in `CohortScriptReference`, and a new `COMMANDS.md`.
- A plugin command takes its arguments typed, refuses with a `CohortScriptRefusal`, and never asks a question.
- The export and the rebuild must agree: every new field needs a writer line, a rebuild test, and a round trip.

## Manual checks before a release

1. Put the package beside the `rdmp` program with a test `Databases.yaml`, and run
   `rdmp cmd ListSupportedCommands`. The six commands and the 13 line commands should be listed.
2. Export a test cohort, check the script, and rebuild it under a new name.
3. In the desktop client, right-click a cohort and run each entry of the **Cohort script** submenu.
4. Read every message a user may see, and check that it names the object and says what to do.
