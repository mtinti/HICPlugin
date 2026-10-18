// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort.Joinables;

namespace Rdmp.LinkedServerCohortSql;

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
    /// <summary>Marks where the composite writes this leaf, so its text is spliced in untabbed (plan 0.7.9.2).</summary>
    public string LeafToken { get; } = $"/*LEAF_{Guid.NewGuid():N}*/";
    /// <summary>Staged shape: the temp table holding this leaf (plan 0.7.2).</summary>
    public string TempTable => (Kind == SetPlacementKind.RemoteWhole ? "#rdmp_r" : "#rdmp_s") + Set.ID;
    /// <summary>Staged shape: the temp table holding a far-side patient index table, shared by its home consumers.</summary>
    public string PitTempTable => Join == null ? null : "#rdmp_pit" + Join.JoinableCohortAggregateConfiguration_ID;
    /// <summary>Staged shape, home leaves: core's select list for the set, in core's order.</summary>
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
