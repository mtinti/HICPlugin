// Copyright (c) The University of Dundee 2024-2024
// This file is part of the Research Data Management Platform (RDMP).
// RDMP is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
// RDMP is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
// You should have received a copy of the GNU General Public License along with RDMP. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.Curation.Data.Cohort.Joinables;
using Rdmp.Core.DataExport.Data;
using RdmpCohortScript;
using Tests.Common;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace HICPluginTests;

/// <summary>
/// The format 1 writers: the build script and the catalogue manifest. These tests read what the writers produce
/// as text and as YAML; building a cohort back from the script is the rebuild's job.
/// </summary>
internal partial class CohortScriptWriterTests : UnitTests
{
    private Catalogue _catalogue;
    private ExtractionInformation _identifier;
    private ExtractionInformation _other;
    private CohortIdentificationConfiguration _cic;
    private CohortAggregateContainer _root;

    [SetUp]
    public void MakeCohort()
    {
        _catalogue = MakeCatalogue("Writer catalogue", out _identifier, out _other);
        _cic = new CohortIdentificationConfiguration(Repository, $"Writer {Guid.NewGuid():N}");
        _cic.CreateRootContainerIfNotExists();
        _root = _cic.RootCohortAggregateContainer;
        _root.Operation = SetOperation.EXCEPT;
        _root.SaveToDatabase();
    }

    // ---- the file

    [Test]
    public void TheScriptIsStrictYamlWithEveryValueDoubleQuoted()
    {
        AddSet(_root, "code = 'A'");

        var document = Writer().Document();

        var plainValues = ScalarsOf(document).Where(s => s.Style != ScalarStyle.DoubleQuoted && !IsKey(s.Value)).ToArray();
        Assert.That(plainValues.Select(s => s.Value), Is.Empty, "Every value should be a double-quoted scalar.");
    }

    [Test]
    public void TheScriptStartsWithTheFormatVersionAndTheCreateLine()
    {
        var document = Writer().Document();
        var yaml = Read(document);

        Assert.Multiple(() =>
        {
            Assert.That(yaml["cohort-script-format"], Is.EqualTo("1"));
            Assert.That(Commands(document)[0], Does.StartWith($"CreateNewCohortIdentificationConfiguration \"{_cic.Name}\" => $cic{_cic.ID} $c{_root.ID} "));
        });
    }

    [Test]
    public void EveryLineParses()
    {
        var set = AddSet(_root, "code = @code");
        AddFilterParameter((AggregateFilter)set.RootFilterContainer.GetFilters().Single(), "DECLARE @code AS varchar(10)", "'A'");
        AddPatientIndexTable();

        foreach (var line in Commands(Writer().Document()))
            Assert.DoesNotThrow(() => ScriptLine.Parse(line), $"The line should parse: {line}");
    }

    // ---- text kept exactly

    [TestCase("notes NOT LIKE '%#tmp%'", TestName = "TextIsKeptExactly(hash sign)")]
    [TestCase("comment = 'note: urgent'", TestName = "TextIsKeptExactly(colon then space)")]
    [TestCase("code =\t'A1'", TestName = "TextIsKeptExactly(tab)")]
    [TestCase("label = 'say \"hi\"'", TestName = "TextIsKeptExactly(double quote)")]
    [TestCase("address = 'line one\nline two'", TestName = "TextIsKeptExactly(LF in a literal)")]
    [TestCase("address = 'line one\r\nline two'", TestName = "TextIsKeptExactly(CRLF in a literal)")]
    [TestCase("label = 'He said \"it''s fine\"'", TestName = "TextIsKeptExactly(both quote kinds)")]
    [TestCase("code IN ('$c1', '$a2')", TestName = "TextIsKeptExactly(text that looks like a handle)")]
    [TestCase("  padded  ", TestName = "TextIsKeptExactly(spaces at both ends)")]
    public void TextIsKeptExactly(string sql)
    {
        AddSet(_root, sql);

        var filterLine = Commands(Writer().Document()).Select(ScriptLine.Parse).Single(l => l.Command == "CreateNewFilter");

        Assert.That(filterLine.Arguments[2].Text, Is.EqualTo(sql), "The filter's SQL should come back unchanged.");
    }

    [Test]
    public void HavingSqlWithACommentLineIsKeptWithItsNewLines()
    {
        var set = AddSet(_root, null);
        set.HavingSQL = "count(*) >= 2 -- at least two\r\nAND max(x) > 5";
        set.SaveToDatabase();

        var having = Commands(Writer().Document()).Select(ScriptLine.Parse)
            .Single(l => l.Command == "Set" && l.Arguments[1].Text == "HavingSQL");

        Assert.That(having.Arguments[2].Text, Is.EqualTo(set.HavingSQL),
            "HAVING SQL should keep its new lines, so the comment does not swallow the next line.");
    }

    // ---- the automatic containers, order and disabled flags

    [Test]
    public void TheRootsFirstTwoChildContainersTakeThePlaceOfTheAutomaticOnesByPosition()
    {
        var first = AddContainer(_root, "Exclusion Criteria", 0);   // named like the second automatic one
        var second = AddContainer(_root, "Custom", 1);
        var third = AddContainer(_root, "Third", 2);

        var lines = Commands(Writer().Document());

        Assert.Multiple(() =>
        {
            Assert.That(lines[0], Does.EndWith($"=> $cic{_cic.ID} $c{_root.ID} $c{first.ID} $c{second.ID}"),
                "The first two child containers should take the automatic places in the order they appear.");
            Assert.That(lines, Has.None.Contains($"AddCohortSubContainer CohortAggregateContainer:$c{_root.ID} => $c{first.ID}"));
            Assert.That(lines, Has.One.EqualTo($"AddCohortSubContainer CohortAggregateContainer:$c{_root.ID} => $c{third.ID}"),
                "A third child container should be added as usual.");
            Assert.That(lines, Has.None.StartWith("Delete"), "Nothing should be deleted when both places are used.");
        });
    }

    [Test]
    public void AnAutomaticContainerTheOriginalLacksIsDeleted()
    {
        AddContainer(_root, "Inclusion Criteria", 0);

        var lines = Commands(Writer().Document());

        Assert.Multiple(() =>
        {
            Assert.That(lines[0], Does.EndWith(" $cx2"), "The unused automatic place gets a handle of its own.");
            Assert.That(lines, Has.One.EqualTo("Delete CohortAggregateContainer:$cx2"));
        });
    }

    [Test]
    public void OrderLinesComeAfterTheChildrenAndDisabledFlagsComeLast()
    {
        var a = AddSet(_root, "x = 1");
        var b = AddSet(_root, "x = 2");
        var child = AddContainer(_root, "Child", 2);
        b.IsDisabled = true;
        b.SaveToDatabase();
        child.IsDisabled = true;
        child.SaveToDatabase();

        var lines = Commands(Writer().Document()).ToList();
        int Index(string start) => lines.FindIndex(l => l.StartsWith(start, StringComparison.Ordinal));

        Assert.Multiple(() =>
        {
            Assert.That(Index($"Set AggregateConfiguration:$a{a.ID} Order"), Is.GreaterThan(Index($"AddCatalogueToCohortSet CohortAggregateContainer:$c{_root.ID}")));
            Assert.That(Index($"Set AggregateConfiguration:$a{b.ID} Order"), Is.GreaterThan(Index($"CreateNewFilter AggregateFilterContainer:$fc{b.RootFilterContainer_ID}")),
                "Order lines should come after every child of the container exists.");
            Assert.That(lines.TakeLast(2), Is.EquivalentTo(new[]
            {
                $"Set AggregateConfiguration:$a{b.ID} IsDisabled true", $"Set CohortAggregateContainer:$c{child.ID} IsDisabled true"
            }), "The disabled flags should be the last lines.");
        });
    }

    // ---- sets, patient index tables and filters

    [Test]
    public void ASetIsAddedWithItsIdentifierColumnAndWithoutMandatoryFilters()
    {
        var set = AddSet(_root, null);

        var line = Commands(Writer().Document()).Single(l => l.StartsWith("AddCatalogueToCohortSet", StringComparison.Ordinal));

        Assert.That(line, Is.EqualTo(
            $"AddCatalogueToCohortSet CohortAggregateContainer:$c{_root.ID} Catalogue:{_catalogue.ID} \"{_catalogue.Name}\" \"{_identifier.GetRuntimeName()}\" false => $a{set.ID} $a{set.ID}.{_identifier.GetRuntimeName()}"));
    }

    [Test]
    public void AFilterCopiedFromAPublishedFilterRecordsItsSource()
    {
        var published = new ExtractionFilter(Repository, "Adults only", _other) { WhereSQL = "age >= 18", IsMandatory = true };
        published.SaveToDatabase();
        var set = AddSet(_root, "age >= 18");
        var filter = (AggregateFilter)set.RootFilterContainer.GetFilters().Single();
        filter.ClonedFromExtractionFilter_ID = published.ID;
        filter.SaveToDatabase();

        var lines = Commands(Writer().Document());

        Assert.That(lines, Has.One.EqualTo($"SetFilterSource AggregateFilter:$f{filter.ID} ExtractionFilter:{published.ID} \"Adults only\""));
    }

    [Test]
    public void ThePatientIndexTableAliasIsMarkedInFilterHavingAndDimensionSql()
    {
        var joinable = AddPatientIndexTable();
        var set = AddSet(_root, $"date >= ix{joinable.ID}.admission_date AND other_ix{joinable.ID} = 1");
        joinable.AddUser(set);
        set.HavingSQL = $"max(ix{joinable.ID}.admission_date) > '2020-01-01'";
        set.SaveToDatabase();
        var dimension = set.AggregateDimensions.Single();
        dimension.SelectSQL = $"COALESCE(chi, ix{joinable.ID}.chi)";
        dimension.SaveToDatabase();

        var parsed = Commands(Writer().Document()).Select(ScriptLine.Parse).ToArray();
        string Arg(Func<ScriptLine, bool> which, int index) => parsed.Single(which).Arguments[index].Text;

        Assert.Multiple(() =>
        {
            Assert.That(Arg(l => l.Command == "CreateNewFilter", 2),
                Is.EqualTo($"date >= ix{{$j{joinable.ID}}}.admission_date AND other_ix{joinable.ID} = 1"),
                "Only the whole word ix<id> should be marked.");
            Assert.That(Arg(l => l.Command == "Set" && l.Arguments[1].Text == "HavingSQL", 2), Does.Contain($"ix{{$j{joinable.ID}}}."));
            Assert.That(Arg(l => l.Command == "Set" && l.Arguments[1].Text == "SelectSQL" &&
                                 l.Arguments[0].Text.StartsWith($"AggregateDimension:$a{set.ID}.", StringComparison.Ordinal), 2),
                Does.Contain($"ix{{$j{joinable.ID}}}."));
        });
    }

    [Test]
    public void APatientIndexTableBindsTheSetTheJoinableAndOneHandlePerColumn()
    {
        var joinable = AddPatientIndexTable();
        var table = joinable.AggregateConfiguration;

        var line = ScriptLine.Parse(Commands(Writer().Document()).Single(l => l.StartsWith("AddPatientIndexTable", StringComparison.Ordinal)));

        Assert.Multiple(() =>
        {
            Assert.That(line.Arguments.Select(a => a.Text).Skip(3), Is.EqualTo(new[] { _identifier.GetRuntimeName(), _other.GetRuntimeName(), "false" }));
            Assert.That(line.Handles, Is.EqualTo(new[]
            {
                $"$a{table.ID}", $"$j{joinable.ID}", $"$a{table.ID}.{_identifier.GetRuntimeName()}", $"$a{table.ID}.{_other.GetRuntimeName()}"
            }));
        });
    }

    // ---- refusals

    [Test]
    public void ASetWithACountColumnIsRefused()
    {
        var set = AddSet(_root, null);
        set.CountSQL = "count(*)";
        set.SaveToDatabase();

        Assert.That(() => Writer().Lines(),
            Throws.TypeOf<CohortScriptExportException>().With.Property(nameof(CohortScriptExportException.Reason)).EqualTo(CohortScriptRefusal.CountColumnOnSet));
    }

    [Test]
    public void ASetWithExtraColumnsIsRefused()
    {
        var set = AddSet(_root, null);
        _ = new AggregateDimension(Repository, _other, set);

        Assert.That(() => Writer().Lines(),
            Throws.TypeOf<CohortScriptExportException>().With.Property(nameof(CohortScriptExportException.Reason)).EqualTo(CohortScriptRefusal.ExtraColumnsOnSet));
    }

    [Test]
    public void SqlThatAlreadyHoldsTheMarkerTextIsRefused()
    {
        AddSet(_root, "label = '{$j1}'");

        Assert.That(() => Writer().Lines(),
            Throws.TypeOf<CohortScriptExportException>().With.Property(nameof(CohortScriptExportException.Reason)).EqualTo(CohortScriptRefusal.MarkerTextInSql));
    }

    // ---- the manifest

    [Test]
    public void TheManifestIsStrictYamlAndListsMandatoryFiltersTablesAndProjects()
    {
        new ExtractionFilter(Repository, "Adults only", _other) { WhereSQL = "age >= 18", IsMandatory = true }.SaveToDatabase();
        var set = AddSet(_root, null);
        var table = WhenIHaveA<TableInfo>();
        Repository.AggregateForcedJoinManager.CreateLinkBetween(set, table);
        var project = WhenIHaveA<Project>();
        _ = new ProjectCohortIdentificationConfigurationAssociation(Repository, project, _cic);

        var manifest = new CatalogueManifestWriter(RepositoryLocator).ForCohort(_cic);
        var yaml = (Dictionary<object, object>)new Deserializer().Deserialize(new StringReader(manifest));
        var catalogue = (Dictionary<object, object>)((List<object>)yaml["catalogues"]).Single();
        var filter = (Dictionary<object, object>)((List<object>)catalogue["filters"]).Single();

        Assert.Multiple(() =>
        {
            Assert.That(ScalarsOf(manifest).Where(s => s.Style != ScalarStyle.DoubleQuoted && !IsKey(s.Value)).Select(s => s.Value),
                Is.Empty, "Every value should be a double-quoted scalar.");
            Assert.That(yaml["catalogue-manifest-format"], Is.EqualTo("1"));
            Assert.That(catalogue["id"], Is.EqualTo(_catalogue.ID.ToString()));
            Assert.That(filter["mandatory"], Is.EqualTo("true"));
            Assert.That(((List<object>)yaml["tables"]).Cast<Dictionary<object, object>>().Select(t => t["id"]), Has.Member(table.ID.ToString()));
            Assert.That(((List<object>)yaml["projects"]).Cast<Dictionary<object, object>>().Select(p => p["id"]), Has.Member(project.ID.ToString()));
        });
    }

    // ---- helpers

    private BuildScriptWriter Writer() => new(_cic, RepositoryLocator);

    private static string[] Commands(string document)
    {
        var yaml = Read(document);
        return ((List<object>)yaml["Commands"]).Cast<string>().ToArray();
    }

    private static Dictionary<object, object> Read(string document) =>
        (Dictionary<object, object>)new Deserializer().Deserialize(new StringReader(document));

    private static IEnumerable<Scalar> ScalarsOf(string document)
    {
        var parser = new Parser(new StringReader(document));
        while (parser.MoveNext())
            if (parser.Current is Scalar scalar)
                yield return scalar;
    }

    private static bool IsKey(string value) => value is "cohort-script-format" or "exported-from" or "cohort-id"
        or "cohort-name" or "rdmp-version" or "plugin-version" or "Commands" or "catalogue-manifest-format"
        or "catalogues" or "id" or "name" or "project-ids" or "identifier-columns" or "columns" or "filters"
        or "mandatory" or "where" or "parameters" or "declaration" or "value" or "comment" or "tables" or "projects";

    private AggregateConfiguration AddSet(CohortAggregateContainer container, string whereSql)
    {
        var set = new AggregateConfiguration(Repository, _catalogue, "Writer set") { CountSQL = null };
        set.SaveToDatabase();
        _ = new AggregateDimension(Repository, _identifier, set);
        _cic.EnsureNamingConvention(set);
        container.AddChild(set, container.GetOrderedContents().Count());
        if (whereSql == null)
            return set;

        var fc = new AggregateFilterContainer(Repository, FilterContainerOperation.AND);
        set.RootFilterContainer_ID = fc.ID;
        set.SaveToDatabase();
        new AggregateFilter(Repository, "A filter", fc) { WhereSQL = whereSql }.SaveToDatabase();
        return set;
    }

    private void AddFilterParameter(AggregateFilter filter, string declaration, string value)
    {
        var parameter = filter.GetFilterFactory().CreateNewParameter(filter, declaration);
        parameter.Value = value;
        parameter.SaveToDatabase();
    }

    private CohortAggregateContainer AddContainer(CohortAggregateContainer parent, string name, int order)
    {
        var container = new CohortAggregateContainer(Repository, SetOperation.UNION) { Name = name, Order = order };
        container.SaveToDatabase();
        parent.AddChild(container);
        container.Order = order;
        container.SaveToDatabase();
        return container;
    }

    private JoinableCohortAggregateConfiguration AddPatientIndexTable()
    {
        var table = new AggregateConfiguration(Repository, _catalogue, "Admissions") { CountSQL = null };
        table.SaveToDatabase();
        _ = new AggregateDimension(Repository, _identifier, table);
        _ = new AggregateDimension(Repository, _other, table);
        _cic.EnsureNamingConvention(table);
        return new JoinableCohortAggregateConfiguration(Repository, _cic, table);
    }

    private Catalogue MakeCatalogue(string name, out ExtractionInformation idEi, out ExtractionInformation otherEi)
    {
        var throwaway = WhenIHaveA(Repository, out otherEi, out var firstEi);
        var cata = throwaway.Catalogue;
        cata.Name = name;
        cata.SaveToDatabase();
        firstEi.IsExtractionIdentifier = true;
        firstEi.SaveToDatabase();
        idEi = firstEi;
        throwaway.DeleteInDatabase();
        return cata;
    }
}
