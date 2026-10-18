// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RdmpLinkedServerCohortSql;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.Repositories;
using Rdmp.Core.ReusableLibraryCode.Checks;

namespace HICPluginTests;

internal sealed class RecordingActivator(IRDMPPlatformRepositoryServiceLocator locator, ICheckNotifier notifier = null)
    : ThrowImmediatelyActivator(locator, notifier)
{
    public bool Interactive { get; set; }
    public override bool IsInteractive => Interactive;
    public List<(string Title, string Message)> Messages { get; } = new();
    public List<(string Prompt, string Description, string Pattern)> FileRequests { get; } = new();
    public List<DialogArgs> Confirmations { get; } = new();
    public FileInfo SelectedFile { get; set; }
    public bool Overwrite { get; set; }
    public override void Show(string title, string message) => Messages.Add((title, message));
    public override FileInfo SelectFile(string prompt, string description, string pattern)
    {
        FileRequests.Add((prompt, description, pattern));
        return SelectedFile;
    }
    public override bool YesNo(DialogArgs args, out bool chosen)
    {
        Confirmations.Add(args);
        chosen = Overwrite;
        return true;
    }
}
