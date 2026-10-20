// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort.Joinables;

namespace RdmpLinkedServerCohortSql;

/// <summary>Where one set runs (<see cref="SetPlacementKind"/>), and the SQL and temp tables the scripts use for it.</summary>
internal sealed class SetPlacement(AggregateConfiguration set, ITableInfo[] setTables,
    JoinableCohortAggregateConfigurationUse join, AggregateConfiguration pit, ITableInfo[] pitTables,
    SetPlacementKind kind)
{
    public AggregateConfiguration Set { get; } = set;
    public ITableInfo[] SetTables { get; } = setTables;
    public JoinableCohortAggregateConfigurationUse Join { get; } = join;
    public AggregateConfiguration Pit { get; } = pit;
    public ITableInfo[] PitTables { get; } = pitTables;
    public SetPlacementKind Kind { get; } = kind;
    public string Placeholder { get; } = $"/*LINKED_{Guid.NewGuid():N}*/";
    /// <summary>Marks where the script for inspection puts this set, so its SQL goes in after indenting.</summary>
    public string LeafToken { get; } = $"/*LEAF_{Guid.NewGuid():N}*/";
    /// <summary>Staged script: the temp table that holds this set.</summary>
    public string TempTable => (Kind == SetPlacementKind.RemoteWhole ? "#rdmp_r" : "#rdmp_s") + Set.ID;
    /// <summary>Staged script: the temp table that holds a patient index table copied from a linked server, shared by the
    /// sets on the home server that use it.</summary>
    public string PitTempTable => Join == null ? null : "#rdmp_pit" + Join.JoinableCohortAggregateConfiguration_ID;
    /// <summary>Staged script, sets on the home server: the columns RDMP's query builder selects for the set, in its order.</summary>
    public IReadOnlyList<CoreColumn> HomeColumns { get; internal set; }
    public RemoteBatchForm RemoteBatchForm { get; internal set; }
    public IReadOnlyList<ResultColumn> ResultColumns { get; internal set; } = Array.Empty<ResultColumn>();
    public string Collation { get; internal set; }
    public string RemoteSql { get; internal set; }
    public string HomeSql { get; internal set; }
    public AggregateConfiguration RemoteAggregate => Kind == SetPlacementKind.RemoteWhole ? Set : Pit;
    public ITableInfo[] RemoteTables { get; } = kind == SetPlacementKind.RemoteWhole
        ? setTables.Concat(pitTables).Distinct().OrderBy(t => t.ID).ToArray()
        : pitTables;
}
