// Adds an SQL parameter to a cohort, a set or a filter. RDMP's own AddParameter cannot make a filter parameter,
// and builds the whole cohort query before it makes a cohort parameter, which fails on a cohort with no sets.
//
//   rdmp cmd AddCohortParameter AggregateFilter:9 "@code" "DECLARE @code AS varchar(10)" "'E11%'" null

using System;
using System.Linq;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.MapsDirectlyToDatabaseTable;
using Rdmp.Core.QueryBuilding;

namespace RdmpCohortScript;

public class ExecuteCommandAddCohortParameter : CohortScriptCommand
{
    private readonly IMapsDirectlyToDatabaseTable _owner;
    private readonly string _declaration;
    private readonly string _value;
    private readonly string _comment;

    public ExecuteCommandAddCohortParameter(IBasicActivateItems activator,
        [DemandsInitialization("Where the parameter goes: a CohortIdentificationConfiguration, an AggregateConfiguration (a set) or an AggregateFilter")]
        IMapsDirectlyToDatabaseTable owner,
        [DemandsInitialization("The parameter's name, starting with @")]
        string name,
        [DemandsInitialization("The full declaration, for example DECLARE @code AS varchar(10)")]
        string declaration,
        [DemandsInitialization("The value, as SQL, for example '2020-01-01' or 90")]
        string value,
        [DemandsInitialization("A comment shown beside the declaration. Give null for none.")]
        string comment) : base(activator)
    {
        _owner = owner;
        _declaration = declaration;
        _value = value;
        _comment = comment;

        if (owner is not (CohortIdentificationConfiguration or AggregateConfiguration or AggregateFilter))
        {
            Refuse(CohortScriptRefusal.NotAParameterOwner,
                $"A parameter can go on a cohort, a set or a filter, not on {owner?.GetType().Name ?? "nothing"}.");
            return;
        }

        if (RefuseIfReadOnly((IMightBeReadOnly)owner, Describe(owner).ToLowerInvariant()))
            return;

        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith('@'))
        {
            Refuse(CohortScriptRefusal.BadParameterName, $"The parameter name \"{name}\" must start with @, for example @code.");
            return;
        }

        if (!string.Equals(DeclaredName(declaration), name, StringComparison.OrdinalIgnoreCase))
        {
            Refuse(CohortScriptRefusal.DeclarationMismatch,
                $"The declaration \"{declaration}\" does not declare {name}. Write it as DECLARE {name} AS <type>.");
            return;
        }

        if (AnyTableSqlParameter.ProhibitedParameterNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            Refuse(CohortScriptRefusal.ReservedParameterName,
                $"RDMP reserves the parameter name {name} for its own use. Choose another name, and change the SQL that uses it.");
            return;
        }

        if (ExistingParameters().Any(p => string.Equals(p.ParameterName, name, StringComparison.OrdinalIgnoreCase)))
            Refuse(CohortScriptRefusal.DuplicateParameter, $"{Describe(owner)} already has a parameter {name}.");
    }

    public override void Execute()
    {
        base.Execute();

        var repository = BasicActivator.RepositoryLocator.CatalogueRepository;
        ISqlParameter parameter = _owner switch
        {
            AggregateFilter filter => filter.GetFilterFactory().CreateNewParameter(filter, _declaration),
            _ => new AnyTableSqlParameter(repository, _owner, _declaration)
        };
        parameter.Value = _value;
        parameter.Comment = _comment;
        parameter.SaveToDatabase();
    }

    // read the name the way RDMP reads it when it builds the query, so the two cannot disagree
    private static string DeclaredName(string declaration)
    {
        try
        {
            return new ConstantParameter(declaration ?? "", null, null, null).ParameterName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private ISqlParameter[] ExistingParameters() => _owner switch
    {
        AggregateFilter filter => filter.GetAllParameters(),
        _ => BasicActivator.RepositoryLocator.CatalogueRepository.GetAllObjects<AnyTableSqlParameter>()
            .Where(p => p.ReferencedObjectType == _owner.GetType().Name && p.ReferencedObjectID == _owner.ID)
            .Cast<ISqlParameter>().ToArray()
    };

    private static string Describe(IMapsDirectlyToDatabaseTable owner) => owner switch
    {
        CohortIdentificationConfiguration => $"Cohort {owner.ID}",
        AggregateConfiguration => $"Set {owner.ID}",
        _ => $"Filter {owner.ID}"
    };
}
