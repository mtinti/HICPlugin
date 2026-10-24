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
using RdmpCohortScript;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using Tests.Common;

namespace HICPluginTests;

/// <summary>
/// The example scripts in CohortScript/Examples read with the plugin's own parser, so the examples, the format
/// document and the parser cannot drift apart.
/// </summary>
internal class CohortScriptExampleTests : UnitTests
{
    // command name -> (arguments, handles); null handles means "any number", for the lines that bind columns
    private static readonly Dictionary<string, (int Arguments, int? Handles)> CommandTable = new()
    {
        ["CreateNewCohortIdentificationConfiguration"] = (1, 4),
        ["LinkCohortToProject"] = (3, 0),
        ["AddCohortParameter"] = (5, 0),
        ["AddCohortSubContainer"] = (1, 1),
        ["AddCatalogueToCohortSet"] = (5, 2),
        ["AddPatientIndexTable"] = (6, null),
        ["UsePatientIndexTable"] = (3, 0),
        ["AddForcedJoin"] = (3, 0),
        ["AddNewFilterContainer"] = (1, 1),
        ["UseTopFilterContainer"] = (1, 1),
        ["AddChildFilterContainer"] = (1, 1),
        ["AddCohortFilter"] = (3, 1),
        ["SetText"] = (3, 0),
        ["SetFilterSource"] = (3, 0),
        ["RenameCohortSet"] = (2, 0),
        ["SetTopX"] = (4, 0),
        ["Set"] = (3, 0),
        ["Delete"] = (1, 0)
    };

    private static IEnumerable<string> Examples() =>
        Directory.GetFiles(Path.Join(TestContext.CurrentContext.TestDirectory, "CohortScript", "Examples"), "*.yaml")
            .Select(Path.GetFileName).OrderBy(n => n);

    [Test]
    public void ThereAreExamplesToCheck()
    {
        Assert.That(Examples().ToArray(), Has.Length.GreaterThanOrEqualTo(3), "The example scripts should be copied beside the tests.");
    }

    [TestCaseSource(nameof(Examples))]
    public void EveryLineOfTheExampleParsesAndMatchesTheCommandTable(string file)
    {
        var text = File.ReadAllText(Path.Join(TestContext.CurrentContext.TestDirectory, "CohortScript", "Examples", file));
        var yaml = (Dictionary<object, object>)new Deserializer().Deserialize(new StringReader(text));
        var lines = ((List<object>)yaml["Commands"]).Cast<string>().ToArray();

        Assert.That(yaml["cohort-script-format"], Is.EqualTo("1"), "The example should declare format 1.");
        foreach (var text1 in lines)
        {
            var line = ScriptLine.Parse(text1);
            Assert.That(CommandTable.ContainsKey(line.Command), Is.True, $"The command of this line is not in the command table: {text1}");
            var (arguments, handles) = CommandTable[line.Command];
            Assert.That(line.Arguments, Has.Count.EqualTo(arguments), $"Wrong number of arguments: {text1}");
            // a patient index table binds its set, its joinable, its identifier and one handle per extra column
            var expectedHandles = handles ??
                                  3 + (line.Arguments[4].IsNull ? 0 : line.Arguments[4].Text.Split(',').Length);
            Assert.That(line.Handles, Has.Count.EqualTo(expectedHandles), $"Wrong number of handles: {text1}");
        }
    }

    [TestCaseSource(nameof(Examples))]
    public void TheExamplePassesTheCheckExceptForItsMadeUpIds(string file)
    {
        var problems = new List<CohortScriptProblem>();
        var lines = CohortScriptReader.Read(
            File.ReadAllText(Path.Join(TestContext.CurrentContext.TestDirectory, "CohortScript", "Examples", file)), problems);
        new CohortScriptChecker(RepositoryLocator, problems).Check(lines, $"Example {Guid.NewGuid():N}");

        // the examples name catalogues, tables, projects and filters that this test platform does not have
        Assert.That(problems.Where(p => p.Reason != CohortScriptRefusal.ReferenceNotFound), Is.Empty,
            "The example should follow every rule of the format.");
    }

    [TestCaseSource(nameof(Examples))]
    public void TheExampleRebuildsAgainstMatchingFixtures(string file) =>
        RebuildAgainstFixtures(File.ReadAllText(Path.Join(TestContext.CurrentContext.TestDirectory, "CohortScript", "Examples", file)), file);

    // the reference is what an agent copies, so its examples, taken together, must rebuild
    [Test]
    public void TheReferenceExamplesRebuildAgainstMatchingFixtures() =>
        RebuildAgainstFixtures("cohort-script-format: \"1\"\nCommands:\n" +
                               string.Concat(CohortScriptReference.Examples.Select(l => $"  - {YamlText.DoubleQuoted(l)}\n")), "reference");

    private void RebuildAgainstFixtures(string text, string file)
    {
        var problems = new List<CohortScriptProblem>();
        var lines = CohortScriptReader.Read(text, problems).Parsed().ToArray();

        // make an object for every id the example names, with the name and columns it gives, and point the id at it
        var newIds = new Dictionary<string, int>();
        foreach (var line in lines)
        {
            var reference = CohortScriptFormat.Commands[line.Parsed.Command].Reference;
            if (reference == null)
                continue;
            var key = line.Parsed.Arguments[reference.IdArgument].Text;
            var name = line.Parsed.Arguments[reference.NameArgument].Text;
            if (reference.TypeName == "Catalogue")
            {
                var catalogue = newIds.TryGetValue(key, out var existing)
                    ? Repository.GetObjectByID<Catalogue>(existing)
                    : new Catalogue(Repository, name);
                AddColumns(catalogue, lines, line);
                newIds[key] = catalogue.ID;
            }
            else if (!newIds.ContainsKey(key))
            {
                newIds[key] = reference.TypeName switch
                {
                    "TableInfo" => new TableInfo(Repository, name).ID,
                    "Project" => new Rdmp.Core.DataExport.Data.Project(Repository, name).ID,
                    "ExtractionFilter" => new ExtractionFilter(Repository, name, AnyColumn()).ID,
                    _ => throw new InvalidOperationException(reference.TypeName)
                };
            }
        }

        var rewritten = System.Text.RegularExpressions.Regex.Replace(text, @"\b(Catalogue|TableInfo|Project|ExtractionFilter):\d+\b",
            m => newIds.TryGetValue(m.Value, out var id) ? $"{m.Groups[1].Value}:{id}" : m.Value);
        var script = new FileInfo(Path.Join(Path.GetTempPath(), $"rdmp-cohortscript-{Guid.NewGuid()}.yaml"));
        File.WriteAllText(script.FullName, rewritten);
        var name1 = $"Example {file} {Guid.NewGuid():N}";
        try
        {
            var activator = new Rdmp.Core.CommandLine.Interactive.ConsoleInputManager(RepositoryLocator,
                Rdmp.Core.ReusableLibraryCode.Checks.ThrowImmediatelyCheckNotifier.Quiet) { DisallowInput = true };
            new ExecuteCommandBuildCohortFromScript(activator, script, name1).Execute();
        }
        finally
        {
            script.Delete();
        }

        var cic = Repository.GetAllObjectsWhere<Rdmp.Core.Curation.Data.Cohort.CohortIdentificationConfiguration>("Name", name1).Single();
        var sets = cic.RootCohortAggregateContainer.GetAllAggregateConfigurationsRecursively();
        Assert.That(sets, Has.Count.EqualTo(lines.Count(l => l.Parsed.Command == "AddCatalogueToCohortSet")),
            "Every set of the example should be built.");

        // the export of the rebuilt cohort, rebuilt and exported again, must not change: names, SQL and structure
        // survive the round trip. The ids are made up, so there is no data to compare patients on.
        var first = new BuildScriptWriter(cic, RepositoryLocator).Lines();
        var second = new BuildScriptWriter(RebuildFromLines(first, $"{name1} again"), RepositoryLocator).Lines();
        Assert.That(Normalised(second), Is.EqualTo(Normalised(first)),
            "Exporting, rebuilding and exporting again should give the same script.");

        // and the first rebuild kept the example's text: every name, SQL, parameter, text value, rename and identifier
        // column of the example is in the export. The writer adds lines the example leaves out (a column's SQL, every
        // container's name), so the example's content must be contained in the export's, not equal to it.
        var missing = Content(lines.Select(l => l.Parsed)).ToList();
        foreach (var item in Content(first.Select(ScriptLine.Parse)))
            missing.Remove(item);
        Assert.That(missing, Is.Empty, "Every text of the example should come back in the export of its rebuild.");
    }

    private Rdmp.Core.Curation.Data.Cohort.CohortIdentificationConfiguration RebuildFromLines(IEnumerable<string> lines, string name)
    {
        var text = "cohort-script-format: \"1\"\nCommands:\n" + string.Concat(lines.Select(l => $"  - {YamlText.DoubleQuoted(l)}\n"));
        var file = new FileInfo(Path.Join(Path.GetTempPath(), $"rdmp-cohortscript-{Guid.NewGuid()}.yaml"));
        File.WriteAllText(file.FullName, text);
        try
        {
            var activator = new Rdmp.Core.CommandLine.Interactive.ConsoleInputManager(RepositoryLocator,
                Rdmp.Core.ReusableLibraryCode.Checks.ThrowImmediatelyCheckNotifier.Quiet) { DisallowInput = true };
            new ExecuteCommandBuildCohortFromScript(activator, file, name).Execute();
        }
        finally
        {
            file.Delete();
        }

        return Repository.GetAllObjectsWhere<Rdmp.Core.Curation.Data.Cohort.CohortIdentificationConfiguration>("Name", name).Single();
    }

    // handles get the numbers of their first appearance, and the cohort name of the create line is dropped, so two
    // exports of equal cohorts compare equal. Only real handles are renamed: handle arguments, the handles after =>,
    // and {$j} markers in SQL; text that merely looks like a handle, such as '$c1' in a literal, is compared as is.
    private static string[] Normalised(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string Rename(string handle)
        {
            if (!map.TryGetValue(handle, out var name))
                map[handle] = name = $"$h{map.Count + 1}";
            return name;
        }

        return lines.Select((text, i) =>
        {
            var line = ScriptLine.Parse(text);
            var definition = CohortScriptFormat.Commands[line.Command];
            var arguments = line.Arguments.Select((a, k) =>
            {
                if (i == 0 && k == 0)
                    return "<name>";
                if (a.IsNull)
                    return "null";
                if (!a.Quoted && definition.ArgumentSpecs[k].Kind == ArgumentKind.Handle &&
                    CohortScriptFormat.TrySplitTyped(a.Text, out var typeName, out var handle))
                    return $"{typeName}:{Rename(handle)}";
                return a.Quoted
                    ? ScriptLine.QuoteText(CohortScriptFormat.SqlMarker.Replace(a.Text, m => "{" + Rename(m.Groups["handle"].Value) + "}"))
                    : a.Text;
            });
            return ScriptLine.Write(line.Command, arguments, line.Handles.Select(h =>
                h.Contains('.') ? $"{Rename(CohortScriptFormat.SplitDimension(h).SetHandle)}.{CohortScriptFormat.SplitDimension(h).Column}" : Rename(h)));
        }).ToArray();
    }

    // the text a script carries, with patient index table markers made equal
    private static IEnumerable<string> Content(IEnumerable<ScriptLine> lines)
    {
        static string T(ScriptArgument a) => a.IsNull ? "<null>" : CohortScriptFormat.SqlMarker.Replace(a.Text, "{$j}");
        foreach (var l in lines)
            switch (l.Command)
            {
                case "AddCohortFilter":
                    yield return $"filter|{T(l.Arguments[1])}|{T(l.Arguments[2])}";
                    break;
                case "AddCohortParameter":
                    yield return $"parameter|{T(l.Arguments[1])}|{T(l.Arguments[2])}|{T(l.Arguments[3])}";
                    break;
                case "SetText":
                    yield return $"text|{l.Arguments[1].Text}|{T(l.Arguments[2])}";
                    break;
                case "RenameCohortSet":
                    yield return $"rename|{T(l.Arguments[1])}";
                    break;
                case "AddCatalogueToCohortSet" or "AddPatientIndexTable":
                    yield return $"identifier|{T(l.Arguments[3])}";
                    break;
            }
    }

    // the columns a line names on its catalogue: the identifier, the extra columns, and the columns of its handles
    private void AddColumns(Catalogue catalogue, IEnumerable<ScriptFileLine> lines, ScriptFileLine line)
    {
        var identifier = line.Parsed.Arguments[3].Text;
        var names = new List<string> { identifier };
        if (line.Parsed.Command == "AddPatientIndexTable" && !line.Parsed.Arguments[4].IsNull)
            names.AddRange(line.Parsed.Arguments[4].Text.Split(','));
        names.AddRange(line.Parsed.Handles.Where(h => h.Contains('.')).Select(h => CohortScriptFormat.SplitDimension(h).Column));

        var existing = catalogue.GetAllExtractionInformation(ExtractionCategory.Any).Select(e => e.GetRuntimeName()).ToHashSet();
        var table = catalogue.GetTableInfoList(true).FirstOrDefault() ?? new TableInfo(Repository, $"[db]..[{catalogue.ID}]");
        foreach (var column in names.Distinct().Where(c => !existing.Contains(c)))
        {
            var columnInfo = new ColumnInfo(Repository, column, "varchar(50)", table);
            var item = new CatalogueItem(Repository, catalogue, column);
            new ExtractionInformation(Repository, item, columnInfo, column) { IsExtractionIdentifier = column == identifier }
                .SaveToDatabase();
        }

        // a column used as an identifier on one line is an identifier for every line on this catalogue
        foreach (var ei in catalogue.GetAllExtractionInformation(ExtractionCategory.Any).Where(e => e.GetRuntimeName() == identifier))
        {
            ei.IsExtractionIdentifier = true;
            ei.SaveToDatabase();
        }
    }

    private ExtractionInformation AnyColumn() =>
        Repository.GetAllObjects<ExtractionInformation>().FirstOrDefault() ??
        WhenIHaveA<ExtractionInformation>();

    [TestCaseSource(nameof(Examples))]
    public void EveryValueOfTheExampleIsDoubleQuoted(string file)
    {
        var parser = new Parser(new StringReader(File.ReadAllText(Path.Join(TestContext.CurrentContext.TestDirectory, "CohortScript", "Examples", file))));
        var keys = new HashSet<string> { "cohort-script-format", "exported-from", "cohort-id", "cohort-name", "rdmp-version", "plugin-version", "Commands" };
        var plain = new List<string>();
        while (parser.MoveNext())
            if (parser.Current is Scalar s && s.Style != ScalarStyle.DoubleQuoted && !keys.Contains(s.Value))
                plain.Add(s.Value);

        Assert.That(plain, Is.Empty, "Every value should be a double-quoted scalar.");
    }
}
