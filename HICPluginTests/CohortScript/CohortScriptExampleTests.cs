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
