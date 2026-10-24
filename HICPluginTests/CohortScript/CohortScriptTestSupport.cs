// Copyright (c) The University of Dundee 2024-2024
// This file is part of the Research Data Management Platform (RDMP).
// RDMP is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
// RDMP is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
// You should have received a copy of the GNU General Public License along with RDMP. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.CommandExecution.AtomicCommands;
using Rdmp.Core.CommandLine.Interactive;
using Rdmp.Core.CommandLine.Interactive.Picking;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.Curation.Data.Cohort.Joinables;
using Rdmp.Core.MapsDirectlyToDatabaseTable;
using RdmpCohortScript;
using Rdmp.Core.Repositories;
using Rdmp.Core.ReusableLibraryCode.Checks;

namespace HICPluginTests;

/// <summary>Runs one command by name with text arguments, the way the rebuild and a typed line run it.</summary>
internal static class CommandLine
{
    // RDMP lists commands from the assemblies already loaded. A real RDMP loads plugins at startup; a test
    // must load the plugin before its first command line.
    static CommandLine() => _ = typeof(RdmpCohortScript.ExecuteCommandAddCatalogueToCohortSet).Assembly;

    public static (bool Completed, string ImpossibleReason) Run(IBasicActivateItems activator, params string[] line)
    {
        var result = RunCommand(activator, line);
        return (result.Completed, result.ImpossibleReason);
    }

    /// <summary>Runs the line and also returns the command, so a test can read its reason code.</summary>
    public static (bool Completed, string ImpossibleReason, IAtomicCommand Command) RunCommand(
        IBasicActivateItems activator, params string[] line)
    {
        var invoker = new CommandInvoker(activator);
        var plugin = typeof(RdmpCohortScript.CohortScriptCommand).Assembly;

        // when RDMP and the plugin share a command name, the plugin's command wins
        var type = invoker.GetSupportedCommands()
            .Where(t => BasicCommandExecution.GetCommandName(t.Name).Equals(line[0], StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Assembly == plugin ? 0 : 1)
            .First();
        var completed = false;
        string reason = null;
        IAtomicCommand command = null;
        invoker.CommandCompleted += (_, e) =>
        {
            completed = true;
            command = e.Command;
        };
        invoker.CommandImpossible += (_, e) =>
        {
            reason = e.Command.ReasonCommandImpossible;
            command = e.Command;
        };
        invoker.ExecuteCommand(type, new CommandLineObjectPicker(line.Skip(1).ToArray(), activator));
        return (completed, reason, command);
    }
}

/// <summary>
/// Runs a build script the way a person types it: each line through RDMP's own command-line reader, with every
/// handle replaced by the id of the object an earlier line made. The person finds those objects by looking at what
/// appeared after each line, so this helper does the same. It hands RDMP the arguments already split, so the limits
/// of typing in a shell (trimmed values, no quote escape) are not exercised here; the format document states them.
/// </summary>
internal static class TypedScript
{
    public static CohortIdentificationConfiguration Run(IBasicActivateItems activator, string document, string newName)
    {
        var repository = activator.RepositoryLocator.CatalogueRepository;
        var problems = new List<CohortScriptProblem>();
        var lines = CohortScriptReader.Read(document, problems).Parsed().ToArray();
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, problems));

        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        CohortIdentificationConfiguration cic = null;
        foreach (var line in lines)
        {
            var definition = CohortScriptFormat.Commands[line.Parsed.Command];
            var typed = new List<string> { line.Parsed.Command };
            for (var i = 0; i < line.Parsed.Arguments.Count; i++)
            {
                var argument = line.Parsed.Arguments[i];
                if (argument.IsNull)
                    typed.Add("null");
                else if (definition.ArgumentSpecs[i].Kind == ArgumentKind.Handle &&
                         CohortScriptFormat.TrySplitTyped(argument.Text, out var typeName, out var handle))
                    typed.Add($"{typeName}:{ids[handle]}");
                else
                    typed.Add(CohortScriptFormat.SqlMarker.Replace(argument.Text, m => ids[m.Groups["handle"].Value].ToString()));
            }

            if (cic == null)
                typed[1] = newName;

            var before = Snapshot(repository);
            var result = CommandLine.Run(activator, typed.ToArray());
            if (!result.Completed)
                throw new InvalidOperationException($"Typed line {line.FileLine} did not run: {result.ImpossibleReason}");

            if (cic == null)
            {
                cic = repository.GetAllObjectsWhere<CohortIdentificationConfiguration>("Name", newName).Single();
                var root = cic.RootCohortAggregateContainer;
                var automatic = root.GetSubContainers().OrderBy(c => c.Order).ToArray();
                ids[line.Parsed.Handles[0]] = cic.ID;
                ids[line.Parsed.Handles[1]] = root.ID;
                ids[line.Parsed.Handles[2]] = automatic[0].ID;
                ids[line.Parsed.Handles[3]] = automatic[1].ID;
                continue;
            }

            var made = New(repository, before);
            foreach (var handle in line.Parsed.Handles)
                ids[handle] = CohortScriptFormat.RoleOf(handle) switch
                {
                    HandleRole.Container => made.Containers.Single(),
                    HandleRole.Set => made.Sets.Single(),
                    HandleRole.Joinable => made.Joinables.Single(),
                    HandleRole.FilterContainer => made.FilterContainers.Single(),
                    HandleRole.Filter => made.Filters.Single(),
                    HandleRole.Dimension => made.Dimensions.Single(id =>
                        string.Equals(repository.GetObjectByID<AggregateDimension>(id).ExtractionInformation.GetRuntimeName(),
                            CohortScriptFormat.SplitDimension(handle).Column, StringComparison.OrdinalIgnoreCase)),
                    _ => throw new InvalidOperationException($"Unexpected handle {handle}")
                };
        }

        return cic;
    }

    private sealed record Ids(int[] Containers, int[] Sets, int[] Joinables, int[] FilterContainers, int[] Filters, int[] Dimensions);

    private static Ids Snapshot(ICatalogueRepository r) => new(
        r.GetAllObjects<CohortAggregateContainer>().Select(o => o.ID).ToArray(),
        r.GetAllObjects<AggregateConfiguration>().Select(o => o.ID).ToArray(),
        r.GetAllObjects<JoinableCohortAggregateConfiguration>().Select(o => o.ID).ToArray(),
        r.GetAllObjects<AggregateFilterContainer>().Select(o => o.ID).ToArray(),
        r.GetAllObjects<AggregateFilter>().Select(o => o.ID).ToArray(),
        r.GetAllObjects<AggregateDimension>().Select(o => o.ID).ToArray());

    private static Ids New(ICatalogueRepository r, Ids before)
    {
        var after = Snapshot(r);
        return new Ids(after.Containers.Except(before.Containers).ToArray(), after.Sets.Except(before.Sets).ToArray(),
            after.Joinables.Except(before.Joinables).ToArray(), after.FilterContainers.Except(before.FilterContainers).ToArray(),
            after.Filters.Except(before.Filters).ToArray(), after.Dimensions.Except(before.Dimensions).ToArray());
    }
}

/// <summary>
/// An interactive activator, as in the GUI. It records every question and refuses it, except a yes/no question,
/// which it answers with <see cref="AnswerYesNo"/>.
/// </summary>
internal sealed class PromptRefusingActivator : ConsoleInputManager
{
    public List<string> Prompts { get; } = new();
    public bool AnswerYesNo { get; set; }

    public PromptRefusingActivator(IRDMPPlatformRepositoryServiceLocator locator)
        : base(locator, ThrowImmediatelyCheckNotifier.Quiet)
    {
        DisallowInput = false;
    }

    private Exception Refuse(string what)
    {
        Prompts.Add(what);
        return new InvalidOperationException($"The command asked a question: {what}");
    }

    public override bool YesNo(DialogArgs args, out bool chosen)
    {
        Prompts.Add($"YesNo: {args.TaskDescription}");
        chosen = AnswerYesNo;
        return true;
    }

    public override bool TypeText(DialogArgs args, int maxLength, string initialText, out string text,
        bool requireSaneHeaderText) => throw Refuse($"TypeText: {args.TaskDescription}");

    public override bool SelectEnum(DialogArgs args, Type enumType, out Enum chosen) =>
        throw Refuse($"SelectEnum: {args.TaskDescription}");

    public override bool SelectType(DialogArgs args, Type[] available, out Type chosen) =>
        throw Refuse($"SelectType: {args.TaskDescription}");

    public override IMapsDirectlyToDatabaseTable SelectOne(DialogArgs args,
        IMapsDirectlyToDatabaseTable[] availableObjects) => throw Refuse($"SelectOne: {args.TaskDescription}");

    public override bool SelectObject<T>(DialogArgs args, T[] available, out T selected) =>
        throw Refuse($"SelectObject: {args.TaskDescription}");

    public override bool SelectObjects<T>(DialogArgs args, T[] available, out T[] selected) =>
        throw Refuse($"SelectObjects: {args.TaskDescription}");

    public override DirectoryInfo SelectDirectory(string prompt) => throw Refuse($"SelectDirectory: {prompt}");

    public override FileInfo SelectFile(string prompt) => throw Refuse($"SelectFile: {prompt}");

    public override FileInfo SelectFile(string prompt, string patternDescription, string pattern) =>
        throw Refuse($"SelectFile: {prompt}");

    public override FileInfo[] SelectFiles(string prompt, string patternDescription, string pattern) =>
        throw Refuse($"SelectFiles: {prompt}");

    public override void Show(string title, string message)
    {
    }
}
