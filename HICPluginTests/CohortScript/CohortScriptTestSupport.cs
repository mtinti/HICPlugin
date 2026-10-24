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
using Rdmp.Core.MapsDirectlyToDatabaseTable;
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
