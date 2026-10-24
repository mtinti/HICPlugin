# Build script commands, format 1

A build script is a YAML file whose Commands list holds one RDMP command per line. Every line is a
double-quoted string. A line is the command, its arguments, and, after =>, the handles it binds:

    AddCatalogueToCohortSet CohortAggregateContainer:$c2 Catalogue:22 "Prescribing" "chi" true => $a2 $a2.chi

Run a script with BuildCohortFromScript, and check it first with CheckCohortScript.

## Handles and existing objects

- A handle names an object the script makes. It is $ with a prefix and a name that starts with a digit
  or x: $cic (the cohort), $c (a container), $a (a set), $j (a patient index table), $fc (a filter
  container) and $f (a filter). A column of a set is the set's handle, a dot and the column name, as
  in $a2.chi.
- A line binds each handle once, and only a later line may use it. A line refers to a handle with its
  type, bare, as in AggregateConfiguration:$a2.
- A script changes only what it makes. It refers to an existing catalogue, table, project or published
  filter by type and id, followed by the object's name in quotes. The check refuses a name that differs
  from the object's current name.
- Text and SQL are in double quotes and kept exactly. The bare word null means no value. Plain values
  (true, false, numbers, words such as INTERSECT) are bare. true and false are in lower case.
- In SQL, ix{$j1} is the alias of patient index table $j1, as in ix{$j1}.admission_date.
- Every parameter a filter uses (@name) is declared with AddCohortParameter where RDMP gives it to the
  filter: on the cohort, on the filter's set or any filter of that set, or on a patient index table the
  set joins (its set or any of its filters). A mandatory filter that a set line copies brings its
  parameters, which count like those of the set's other filters. A parameter of another set does not
  count, because a set run alone would fail on it.

## Order of lines

1. The first line creates the cohort. RDMP makes three containers with it: the root (EXCEPT), Inclusion
   Criteria and Exclusion Criteria (both UNION). The line binds the cohort and the three containers, in
   that order. A script reuses them, and deletes one it does not need with Delete.
2. LinkCohortToProject comes before any set on a catalogue that is specific to that project.
3. A patient index table comes before every set that joins it.
4. RDMP adds each new child at the top of its container. After every child of a container exists, one
   Set ... Order line per child gives the order. The order matters for EXCEPT.
5. IsDisabled lines come last, after the structure is built.

## Mandatory filters

A catalogue's mandatory filters are the filters that every use of it should apply. The catalogue
manifest marks them. The last argument of AddCatalogueToCohortSet and AddPatientIndexTable says whether
to copy them into the new set. A new cohort normally copies them: write true. An exported script
writes false, because it already holds the filters as the cohort saved them, each with a SetFilterSource
line naming the mandatory filter it came from. When you start from an exported script, write true and
leave out those filters. CheckCohortScript with its second argument true warns about every false on a
catalogue that has mandatory filters.

When RDMP copies mandatory filters, it puts them in a new top filter container (AND) of the set, and
AddNewFilterContainer then fails, because a set has one top filter container. After a line that
writes true, use UseTopFilterContainer: it binds the container the set has, or makes one when the
catalogue has no mandatory filters. The set's own filters then go beside the mandatory ones.

## Commands

### CreateNewCohortIdentificationConfiguration

Creates the cohort. RDMP's own command.

Arguments:

1. name (text in quotes, or null): The new cohort's name. BuildCohortFromScript replaces it with the name it is given.

Binds: the cohort ($cic), the root container ($c), Inclusion Criteria ($c), Exclusion Criteria ($c).

Example:

    CreateNewCohortIdentificationConfiguration "Diabetes since 2010" => $cic1 $c1 $c2 $c3

### LinkCohortToProject

Links the cohort to an existing project.

Arguments:

1. cic (handle: CohortIdentificationConfiguration:$...): The cohort to link
2. project (existing object: Type:id): The project, by id
3. projectName (text in quotes, or null): The project's name, to check the id. Give null to skip the check.

In a build script, argument 3 always gives the project's name, so the check can catch a wrong id.

Example:

    LinkCohortToProject CohortIdentificationConfiguration:$cic1 Project:5 "Diabetes study"

### SetText

Sets a text property to exactly the text given.

Arguments:

1. target (handle: CohortIdentificationConfiguration:$... or CohortAggregateContainer:$... or AggregateConfiguration:$... or AggregateDimension:$... or AggregateFilter:$...): The cohort object to change: a cohort, container, set, column of a set, or filter
2. property (plain word): The text property to set, for example Description or HavingSQL
3. text (text or SQL in quotes, or null): The text, exactly as it should be stored. Give null for none.

Properties it may change:

- CohortIdentificationConfiguration: Description, Ticket, Folder
- CohortAggregateContainer: Name
- AggregateConfiguration: HavingSQL, CountSQL, Description
- AggregateDimension: SelectSQL, Alias
- AggregateFilter: Description

Example:

    SetText CohortIdentificationConfiguration:$cic1 Description "People with diabetes, diagnosed since 2010"

### AddCohortParameter

Declares a parameter on the cohort, a set or a filter, with its value.

Arguments:

1. owner (handle: CohortIdentificationConfiguration:$... or AggregateConfiguration:$... or AggregateFilter:$...): Where the parameter goes: a CohortIdentificationConfiguration, an AggregateConfiguration (a set) or an AggregateFilter
2. name (text in quotes, or null): The parameter's name, starting with @
3. declaration (SQL in quotes, or null): The full declaration, for example DECLARE @code AS varchar(10)
4. value (SQL in quotes, or null): The value, as SQL, for example '2020-01-01' or 90
5. comment (text in quotes, or null): A comment shown beside the declaration. Give null for none.

Example:

    AddCohortParameter CohortIdentificationConfiguration:$cic1 "@since" "DECLARE @since AS date" "'2010-01-01'" "Start of the study"
    AddCohortParameter AggregateFilter:$f3 "@admission_type" "DECLARE @admission_type AS varchar(20)" "'Emergency'" "The kind of admission counted"

### AddPatientIndexTable

Adds a patient index table to the cohort: a set that other sets join, with extra columns.

Arguments:

1. cic (handle: CohortIdentificationConfiguration:$...): The cohort the patient index table belongs to
2. catalogue (existing object: Type:id): The catalogue the patient index table reads, by id
3. catalogueName (text in quotes, or null): The catalogue's name, to check the id. Give null to skip the check.
4. identifierColumn (text in quotes, or null): The identifier column to use, by its name in the catalogue. Give null when the catalogue has only one.
5. extraColumns (text in quotes, or null): The other columns to select, by name, separated by commas (so a name cannot contain a comma). Give null for none.
6. importMandatoryFilters (plain word): true to copy the catalogue's current mandatory filters into the set, false to leave them out

In a build script, argument 3 always gives the catalogue's name, so the check can catch a wrong id.

Binds: the set ($a), the patient index table ($j), the identifier column ($a.column), then one column ($a.column) for each extra column.

Example:

    AddPatientIndexTable CohortIdentificationConfiguration:$cic1 Catalogue:21 "Hospital admissions" "chi" "admission_date" false => $a1 $j1 $a1.chi $a1.admission_date

### AddNewFilterContainer

Gives a set its top filter container. RDMP's own command.

Arguments:

1. host (handle: AggregateConfiguration:$...): The set that gets the filter container. A set has one top filter container.

Binds: the filter container ($fc).

Example:

    AddNewFilterContainer AggregateConfiguration:$a1 => $fc3

### AddCohortFilter

Adds a filter to a filter container, with exactly the name and SQL given.

Arguments:

1. container (handle: AggregateFilterContainer:$...): The filter container the filter goes into
2. name (text in quotes, or null): The filter's name, exactly as it should be stored
3. whereSql (SQL in quotes, or null): The filter's SQL, exactly as it should be stored. Give null for none.

Binds: the filter ($f).

Example:

    AddCohortFilter AggregateFilterContainer:$fc3 "Emergency admissions" "admission_type = @admission_type" => $f3
    AddCohortFilter AggregateFilterContainer:$fc1 "After an admission" "prescribed_date > ix{$j1}.admission_date AND prescribed_date >= @since" => $f1
    AddCohortFilter AggregateFilterContainer:$fc2 "Metformin" "drug_name = 'Metformin'" => $f2

### Set

Sets a plain value: an operation, an order, or true or false. RDMP's own command.

Arguments:

1. setOn (handle: CohortAggregateContainer:$... or AggregateConfiguration:$... or AggregateDimension:$... or AggregateFilterContainer:$... or AggregateFilter:$...): The object to change
2. property (plain word): The property to change. The list below gives the properties a build script may change.
3. value (plain word): The new value: an operation (UNION, INTERSECT, EXCEPT, AND, OR), a number, or true or false

Properties it may change:

- CohortAggregateContainer: Operation, Order, IsDisabled
- AggregateConfiguration: Order, IsDisabled
- AggregateDimension: GroupBy
- AggregateFilterContainer: Operation, IsDisabled
- AggregateFilter: IsDisabled

Example:

    Set CohortAggregateContainer:$c2 Operation INTERSECT
    Set AggregateFilterContainer:$fc2 Operation OR

### AddCatalogueToCohortSet

Adds a set to a container: one catalogue, through one identifier column.

Arguments:

1. container (handle: CohortAggregateContainer:$...): The container the set goes into
2. catalogue (existing object: Type:id): The catalogue the set reads, by id
3. catalogueName (text in quotes, or null): The catalogue's name, to check the id. Give null to skip the check.
4. identifierColumn (text in quotes, or null): The identifier column to use, by its name in the catalogue. Give null when the catalogue has only one.
5. importMandatoryFilters (plain word): true to copy the catalogue's current mandatory filters into the set, false to leave them out

In a build script, argument 3 always gives the catalogue's name, so the check can catch a wrong id.

Binds: the set ($a), the identifier column ($a.column).

Example:

    AddCatalogueToCohortSet CohortAggregateContainer:$c2 Catalogue:22 "Prescribing" "chi" true => $a2 $a2.chi

### RenameCohortSet

Renames a set. RDMP keeps its cic_<id>_ prefix in front of the name.

Arguments:

1. set (handle: AggregateConfiguration:$...): The set to rename
2. name (text in quotes, or null): The new name, without RDMP's cic_<id>_ prefix

Example:

    RenameCohortSet AggregateConfiguration:$a2 "Prescribed after an admission"

### UsePatientIndexTable

Makes a set join a patient index table.

Arguments:

1. set (handle: AggregateConfiguration:$...): The set that joins the patient index table
2. joinable (handle: JoinableCohortAggregateConfiguration:$...): The patient index table, by its joinable
3. joinType (plain word): How the set joins the table: Inner, Left or Right

Example:

    UsePatientIndexTable AggregateConfiguration:$a2 JoinableCohortAggregateConfiguration:$j1 Inner

### AddForcedJoin

Makes a set join one more table of its catalogue, even when no column needs it.

Arguments:

1. set (handle: AggregateConfiguration:$...): The set whose query gets the table
2. table (existing object: Type:id): The table to join, by id
3. tableName (text in quotes, or null): The table's name, to check the id. Give null to skip the check.

In a build script, argument 3 always gives the table's name, so the check can catch a wrong id.

Example:

    AddForcedJoin AggregateConfiguration:$a2 TableInfo:31 "[Data]..[DrugLookup]"

### UseTopFilterContainer

Binds a set's top filter container, and makes one (AND) when the set has none. Use it after a set line that copies mandatory filters.

Arguments:

1. set (handle: AggregateConfiguration:$...): The set whose top filter container the script uses. The command makes one (AND) when the set has none.

Binds: the filter container ($fc).

Example:

    UseTopFilterContainer AggregateConfiguration:$a2 => $fc1

### AddChildFilterContainer

Adds a filter container inside a filter container.

Arguments:

1. parent (handle: AggregateFilterContainer:$...): The filter container the new child goes under

Binds: the filter container ($fc).

Example:

    AddChildFilterContainer AggregateFilterContainer:$fc1 => $fc2

### SetFilterSource

Records which published filter a filter was copied from.

Arguments:

1. filter (handle: AggregateFilter:$...): The cohort filter
2. source (existing object: Type:id): The published filter it was copied from, by id
3. sourceName (text in quotes, or null): The published filter's name, to check the id. Give null to skip the check.

In a build script, argument 3 always gives the published filter's name, so the check can catch a wrong id.

Example:

    SetFilterSource AggregateFilter:$f2 ExtractionFilter:41 "Metformin"

### SetTopX

Keeps only the first rows of a set.

Arguments:

1. set (handle: AggregateConfiguration:$...): The set
2. topX (plain word): How many rows to keep
3. orderBy (handle: AggregateDimension:$..., or null): The column that orders the rows in the set's graph. Give null to order the graph by the count column. RDMP 9.3.0 writes no ORDER BY in a cohort query, so this does not decide which patients a cohort set keeps.
4. direction (plain word): Ascending or Descending

Example:

    SetTopX AggregateConfiguration:$a2 100 AggregateDimension:$a2.chi Descending

### AddCohortSubContainer

Adds a container inside a container. RDMP's own command.

Arguments:

1. container (handle: CohortAggregateContainer:$...): The container the new container goes into

Binds: a container ($c).

Example:

    AddCohortSubContainer CohortAggregateContainer:$c1 => $c4

### Delete

Deletes a container the script made. In a build script, only RDMP's automatic containers. RDMP's own command.

Arguments:

1. deletables (handle: CohortAggregateContainer:$...): The container to delete: one of the three the first line made, which the cohort does not use

Example:

    Delete CohortAggregateContainer:$c3
