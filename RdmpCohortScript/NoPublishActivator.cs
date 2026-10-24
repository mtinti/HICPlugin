// The activator the rebuild hands to each command it runs. After most commands RDMP loads its whole list of
// objects again (the child provider), and in the Windows client every open tree rebuilds too. A rebuild runs many
// commands, and none of them needs the list refreshed between lines: they find objects in the database. So this
// wrapper forwards every member to the real activator except five:
//
//   Publish, PublishNearest   do nothing. The build command refreshes once, at the end, through the real activator.
//   DeleteWithConfirmation    deletes without the refresh RDMP does inside its own version. When the real
//                             activator would ask the user first, it forwards, so the question is still asked
//                             and RDMP then refreshes as usual. A build turns interactive deletes off, so it
//                             never takes that path.
//   RequestItemEmphasis       does nothing. The new objects are not in the list yet, and a later line may delete them.
//   Activate                  does nothing. The create command opens the new cohort, which would show it half built,
//                             and in the terminal client it waits until the user closes the window.
//
// RDMP's own NoPublish switch on each command is not enough: a command made inside another command, and Delete,
// refresh without checking it.

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FAnsi.Discovery;
using Rdmp.Core;
using Rdmp.Core.CohortCommitting.Pipeline;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.CommandLine.Runners;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.Curation.Data.DataLoad;
using Rdmp.Core.Curation.Data.Defaults;
using Rdmp.Core.Curation.Data.Pipelines;
using Rdmp.Core.DataExport.Data;
using Rdmp.Core.DataViewing;
using Rdmp.Core.Icons.IconProvision;
using Rdmp.Core.Logging;
using Rdmp.Core.MapsDirectlyToDatabaseTable;
using Rdmp.Core.MapsDirectlyToDatabaseTable.Versioning;
using Rdmp.Core.Providers;
using Rdmp.Core.Repositories;
using Rdmp.Core.ReusableLibraryCode.Checks;
using Rdmp.Core.ReusableLibraryCode.Comments;

namespace RdmpCohortScript;

/// <summary>
/// Forwards to the real activator, but does not refresh RDMP's list of objects, and does not show or open
/// objects, while a rebuild runs its lines.
/// </summary>
internal sealed class NoPublishActivator : IBasicActivateItems
{
    private readonly IBasicActivateItems _real;

    public NoPublishActivator(IBasicActivateItems real)
    {
        _real = real ?? throw new ArgumentNullException(nameof(real));
    }

    // ---- the five members the wrapper handles itself

    public void Publish(IMapsDirectlyToDatabaseTable databaseEntity)
    {
    }

    public void PublishNearest(object publish)
    {
    }

    public bool DeleteWithConfirmation(IDeleteable deletable)
    {
        // the condition RDMP's own version tests; the rebuild turns interactive deletes off, so it does not ask
        if (_real.IsInteractive && _real.InteractiveDeletes)
            return _real.DeleteWithConfirmation(deletable);

        deletable.DeleteInDatabase();
        return true;
    }

    public void RequestItemEmphasis(object sender, EmphasiseRequest emphasiseRequest)
    {
    }

    public void Activate(object o)
    {
    }

    // ---- everything else is forwarded unchanged

    public bool IsInteractive => _real.IsInteractive;
    public List<IPluginUserInterface> PluginUserInterfaces => _real.PluginUserInterfaces;
    public CommentStore CommentStore => _real.CommentStore;
    public void ShowGraph(AggregateConfiguration aggregate) => _real.ShowGraph(aggregate);
    public void ShowLogs(ILoggedActivityRootObject rootObject) => _real.ShowLogs(rootObject);
    public void ShowLogs(ExternalDatabaseServer loggingServer, LogViewerFilter filter) => _real.ShowLogs(loggingServer, filter);
    public bool CanActivate(object o) => _real.CanActivate(o);

    public bool InteractiveDeletes
    {
        get => _real.InteractiveDeletes;
        set => _real.InteractiveDeletes = value;
    }

    public event EmphasiseItemHandler Emphasise
    {
        add => _real.Emphasise += value;
        remove => _real.Emphasise -= value;
    }

    public void ShowData(IViewSQLAndResultsCollection collection) => _real.ShowData(collection);
    public void ShowData(DataTable dataTable) => _real.ShowData(dataTable);
    public IRepository GetRepositoryFor(Type type) => _real.GetRepositoryFor(type);
    public ICoreChildProvider CoreChildProvider => _real.CoreChildProvider;
    public IServerDefaults ServerDefaults => _real.ServerDefaults;
    public FavouritesProvider FavouritesProvider => _real.FavouritesProvider;
    public ICoreIconProvider CoreIconProvider => _real.CoreIconProvider;
    public List<CommandInvokerDelegate> GetDelegates() => _real.GetDelegates();
    public IRDMPPlatformRepositoryServiceLocator RepositoryLocator => _real.RepositoryLocator;
    public IEnumerable<Type> GetIgnoredCommands() => _real.GetIgnoredCommands();

    public IPipelineRunner GetPipelineRunner(DialogArgs args, IPipelineUseCase useCase, IPipeline pipeline) =>
        _real.GetPipelineRunner(args, useCase, pipeline);

    public CohortCreationRequest GetCohortCreationRequest(ExternalCohortTable externalCohortTable, IProject project,
        string cohortInitialDescription) =>
        _real.GetCohortCreationRequest(externalCohortTable, project, cohortInitialDescription);

    public IProject CohortCommitProjectSelect(IProject currentProject, Project[] projects, CohortIdentificationConfiguration cic) =>
        _real.CohortCommitProjectSelect(currentProject, projects, cic);

    public CohortHoldoutLookupRequest GetCohortHoldoutLookupRequest(ExternalCohortTable externalCohortTable, IProject project,
        CohortIdentificationConfiguration cic) =>
        _real.GetCohortHoldoutLookupRequest(externalCohortTable, project, cic);

    public void SelectAnythingThen(string prompt, Action<IMapsDirectlyToDatabaseTable> callback) => _real.SelectAnythingThen(prompt, callback);
    public void SelectAnythingThen(DialogArgs args, Action<IMapsDirectlyToDatabaseTable> callback) => _real.SelectAnythingThen(args, callback);

    public IMapsDirectlyToDatabaseTable[] SelectMany(string prompt, Type arrayElementType,
        IMapsDirectlyToDatabaseTable[] availableObjects, string initialSearchText = null) =>
        _real.SelectMany(prompt, arrayElementType, availableObjects, initialSearchText);

    public IMapsDirectlyToDatabaseTable[] SelectMany(DialogArgs args, Type arrayElementType,
        IMapsDirectlyToDatabaseTable[] availableObjects) =>
        _real.SelectMany(args, arrayElementType, availableObjects);

    public IMapsDirectlyToDatabaseTable SelectOne(string prompt, IMapsDirectlyToDatabaseTable[] availableObjects,
        string initialSearchText = null, bool allowAutoSelect = false) =>
        _real.SelectOne(prompt, availableObjects, initialSearchText, allowAutoSelect);

    public IMapsDirectlyToDatabaseTable SelectOne(DialogArgs args, IMapsDirectlyToDatabaseTable[] availableObjects) =>
        _real.SelectOne(args, availableObjects);

    public bool SelectObject<T>(string prompt, T[] available, out T selected, string initialSearchText = null,
        bool allowAutoSelect = false) where T : class =>
        _real.SelectObject(prompt, available, out selected, initialSearchText, allowAutoSelect);

    public bool SelectObject<T>(DialogArgs args, T[] available, out T selected) where T : class =>
        _real.SelectObject(args, available, out selected);

    public bool SelectObjects<T>(string prompt, T[] available, out T[] selected, string initialSearchText = null)
        where T : class =>
        _real.SelectObjects(prompt, available, out selected, initialSearchText);

    public bool SelectObjects<T>(DialogArgs args, T[] available, out T[] selected) where T : class =>
        _real.SelectObjects(args, available, out selected);

    public DirectoryInfo SelectDirectory(string prompt) => _real.SelectDirectory(prompt);
    public FileInfo SelectFile(string prompt) => _real.SelectFile(prompt);
    public FileInfo[] SelectFiles(string prompt, string patternDescription, string pattern) => _real.SelectFiles(prompt, patternDescription, pattern);
    public FileInfo SelectFile(string prompt, string patternDescription, string pattern) => _real.SelectFile(prompt, patternDescription, pattern);

    public bool SelectValueType(string prompt, Type paramType, object initialValue, out object chosen) =>
        _real.SelectValueType(prompt, paramType, initialValue, out chosen);

    public bool SelectValueType(DialogArgs args, Type paramType, object initialValue, out object chosen) =>
        _real.SelectValueType(args, paramType, initialValue, out chosen);

    public bool YesNo(string text, string caption, out bool chosen) => _real.YesNo(text, caption, out chosen);
    public bool YesNo(DialogArgs args, out bool chosen) => _real.YesNo(args, out chosen);
    public bool YesNo(string text, string caption) => _real.YesNo(text, caption);
    public bool YesNo(DialogArgs args) => _real.YesNo(args);
    public bool Confirm(string text, string caption) => _real.Confirm(text, caption);
    public bool Confirm(DialogArgs args) => _real.Confirm(args);
    public void Show(string message) => _real.Show(message);
    public void ShowWarning(string message) => _real.ShowWarning(message);
    public void Show(string title, string message) => _real.Show(title, message);

    public bool TypeText(string header, string prompt, int maxLength, string initialText, out string text,
        bool requireSaneHeaderText) =>
        _real.TypeText(header, prompt, maxLength, initialText, out text, requireSaneHeaderText);

    public bool TypeText(DialogArgs args, int maxLength, string initialText, out string text, bool requireSaneHeaderText) =>
        _real.TypeText(args, maxLength, initialText, out text, requireSaneHeaderText);

    public DiscoveredDatabase SelectDatabase(bool allowDatabaseCreation, string taskDescription) =>
        _real.SelectDatabase(allowDatabaseCreation, taskDescription);

    public DiscoveredTable SelectTable(bool allowDatabaseCreation, string taskDescription) =>
        _real.SelectTable(allowDatabaseCreation, taskDescription);

    public void ShowException(string errorText, Exception exception) => _real.ShowException(errorText, exception);
    public void Wait(string title, Task task, CancellationTokenSource cts) => _real.Wait(title, task, cts);
    public bool SelectEnum(string prompt, Type enumType, out Enum chosen) => _real.SelectEnum(prompt, enumType, out chosen);
    public bool SelectEnum(DialogArgs args, Type enumType, out Enum chosen) => _real.SelectEnum(args, enumType, out chosen);
    public bool SelectType(string prompt, Type baseTypeIfAny, out Type chosen) => _real.SelectType(prompt, baseTypeIfAny, out chosen);
    public bool SelectType(DialogArgs args, Type baseTypeIfAny, out Type chosen) => _real.SelectType(args, baseTypeIfAny, out chosen);

    public bool SelectType(string prompt, Type baseTypeIfAny, bool allowAbstract, bool allowInterfaces, out Type chosen) =>
        _real.SelectType(prompt, baseTypeIfAny, allowAbstract, allowInterfaces, out chosen);

    public bool SelectType(DialogArgs args, Type baseTypeIfAny, bool allowAbstract, bool allowInterfaces, out Type chosen) =>
        _real.SelectType(args, baseTypeIfAny, allowAbstract, allowInterfaces, out chosen);

    public bool SelectType(string prompt, Type[] available, out Type chosen) => _real.SelectType(prompt, available, out chosen);
    public bool SelectType(DialogArgs args, Type[] available, out Type chosen) => _real.SelectType(args, available, out chosen);

    public ICatalogue CreateAndConfigureCatalogue(ITableInfo tableInfo, ColumnInfo[] extractionIdentifierColumns,
        string initialDescription, IProject projectSpecific, string targetFolder) =>
        _real.CreateAndConfigureCatalogue(tableInfo, extractionIdentifierColumns, initialDescription, projectSpecific, targetFolder);

    public bool ShowCohortWizard(out CohortIdentificationConfiguration cic) => _real.ShowCohortWizard(out cic);
    public object GetRootObjectOrSelf(object objectToEmphasise) => _real.GetRootObjectOrSelf(objectToEmphasise);

    public ExternalDatabaseServer CreateNewPlatformDatabase(ICatalogueRepository catalogueRepository,
        PermissableDefaults defaultToSet, IPatcher patcher, DiscoveredDatabase db) =>
        _real.CreateNewPlatformDatabase(catalogueRepository, defaultToSet, patcher, db);

    public IEnumerable<T> GetAll<T>() => _real.GetAll<T>();
    public IEnumerable<IMapsDirectlyToDatabaseTable> GetAll(Type t) => _real.GetAll(t);
    public ICheckNotifier GlobalErrorCheckNotifier => _real.GlobalErrorCheckNotifier;

    public bool HardRefresh
    {
        get => _real.HardRefresh;
        set => _real.HardRefresh = value;
    }

    public bool IsWinForms => _real.IsWinForms;
    public bool IsAbleToLaunchSubprocesses => _real.IsAbleToLaunchSubprocesses;
    public void LaunchSubprocess(ProcessStartInfo startInfo) => _real.LaunchSubprocess(startInfo);
    public bool UseCommits() => _real.UseCommits();
}
