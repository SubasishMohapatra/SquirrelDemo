using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading;
using System.Threading.Tasks;
using NuGet;
using NuGet.VisualStudio;
using NuGet.VisualStudio.Resources;

namespace NuGetConsole.Host.PowerShell.Implementation
{
    internal abstract class PowerShellHost : IHost, IPathExpansion, IDisposable
    {
        private static readonly object _initScriptsLock = new object();
        private readonly string _name;
        private readonly IRunspaceManager _runspaceManager;
        private readonly IVsPackageSourceProvider _packageSourceProvider;
        private readonly ISolutionManager _solutionManager;

        private IConsole _activeConsole;
        private RunspaceDispatcher _runspace;
        private NuGetPSHost _nugetHost;
        // indicates whether this host has been initialized. 
        // null = not initilized, true = initialized successfully, false = initialized unsuccessfully
        private bool? _initialized;
        // store the current (non-truncated) project names displayed in the project name combobox
        private string[] _projectSafeNames;

        // store the current command typed so far
        private ComplexCommand _complexCommand;

        protected PowerShellHost(string name, IRunspaceManager runspaceManager)
        {
            _runspaceManager = runspaceManager;

            // TODO: Take these as ctor arguments
            _packageSourceProvider = ServiceLocator.GetInstance<IVsPackageSourceProvider>();
            _solutionManager = ServiceLocator.GetInstance<ISolutionManager>();

            _name = name;
            IsCommandEnabled = true;
        }

        protected Pipeline ExecutingPipeline { get; set; }

        /// <summary>
        /// The host is associated with a particular console on a per-command basis. 
        /// This gets set every time a command is executed on this host.
        /// </summary>
        protected IConsole ActiveConsole
        {
            get
            {
                return _activeConsole;
            }
            set
            {
                _activeConsole = value;
                if (_nugetHost != null)
                {
                    _nugetHost.ActiveConsole = value;
                }
            }
        }

        public bool IsCommandEnabled
        {
            get;
            private set;
        }

        protected RunspaceDispatcher Runspace
        {
            get
            {
                return _runspace;
            }
        }

        private ComplexCommand ComplexCommand
        {
            get
            {
                if (_complexCommand == null)
                {
                    _complexCommand = new ComplexCommand((allLines, lastLine) =>
                    {
                        Collection<PSParseError> errors;
                        PSParser.Tokenize(allLines, out errors);

                        // If there is a parse error token whose END is past input END, consider
                        // it a multi-line command.
                        if (errors.Count > 0)
                        {
                            if (errors.Any(e => (e.Token.Start + e.Token.Length) >= allLines.Length))
                            {
                                return false;
                            }
                        }

                        return true;
                    });
                }
                return _complexCommand;
            }
        }

        public string Prompt
        {
            get
            {
                return ComplexCommand.IsComplete ? EvaluatePrompt() : ">> ";
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private string EvaluatePrompt()
        {
            string prompt = "PM>";

            try
            {
                PSObject output = this.Runspace.Invoke("prompt", null, outputResults: false).FirstOrDefault();
                if (output != null)
                {
                    string result = output.BaseObject.ToString();
                    if (!String.IsNullOrEmpty(result))
                    {
                        prompt = result;
                    }
                }
            }
            catch (Exception ex)
            {
                ExceptionHelper.WriteToActivityLog(ex);
            }
            return prompt;
        }

        /// <summary>
        /// Doing all necessary initialization works before the console accepts user inputs
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public void Initialize(IConsole console)
        {
            ActiveConsole = console;

            if (_initialized.HasValue)
            {
                if (_initialized.Value && console.ShowDisclaimerHeader)
                {
                    DisplayDisclaimerAndHelpText();
                }
            }
            else
            {
                try
                {
                    Tuple<RunspaceDispatcher, NuGetPSHost> result = _runspaceManager.GetRunspace(console, _name);
                    _runspace = result.Item1;
                    _nugetHost = result.Item2;
                    
                    _initialized = true;

                    if (console.ShowDisclaimerHeader)
                    {
                        DisplayDisclaimerAndHelpText();
                    }

                    UpdateWorkingDirectory();
                    ExecuteInitScripts();

                    // Hook up solution events
                    _solutionManager.SolutionOpened += (o, e) =>
                    {
                        Task.Factory.StartNew(() =>
                            {
                                UpdateWorkingDirectory();
                                ExecuteInitScripts();
                            }, 
                            CancellationToken.None,
                            TaskCreationOptions.None,
                            TaskScheduler.Default);
                    };
                    _solutionManager.SolutionClosed += (o, e) => UpdateWorkingDirectory();
                }
                catch (Exception ex)
                {
                    // catch all exception as we don't want it to crash VS
                    _initialized = false;
                    IsCommandEnabled = false;
                    ReportError(ex);

                    ExceptionHelper.WriteToActivityLog(ex);
                }
            }
        }

        private void UpdateWorkingDirectory()
        {
            if (Runspace.RunspaceAvailability == RunspaceAvailability.Available)
            {
                // if there is no solution open, we set the active directory to be user profile folder
                string targetDir = _solutionManager.IsSolutionOpen ?
                    _solutionManager.SolutionDirectory :
                    Environment.GetEnvironmentVariable("USERPROFILE");

                Runspace.ChangePSDirectory(targetDir);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "We don't want execution of init scripts to crash our console.")]
        private void ExecuteInitScripts()
        {
            // Fix for Bug 1426 Disallow ExecuteInitScripts from being executed concurrently by multiple threads.
            lock (_initScriptsLock)
            {
                if (!_solutionManager.IsSolutionOpen)
                {
                    return;
                }

                IRepositorySettings repositorySettings = ServiceLocator.GetInstance<IRepositorySettings>();
                Debug.Assert(repositorySettings != null);
                if (repositorySettings == null)
                {
                    return;
                }

                try
                {
                    var localRepository = new SharedPackageRepository(repositorySettings.RepositoryPath);

                    // invoke init.ps1 files in the order of package dependency.
                    // if A -> B, we invoke B's init.ps1 before A's.

                    var sorter = new PackageSorter(targetFramework: null);
                    var sortedPackages = sorter.GetPackagesByDependencyOrder(localRepository);

                    foreach (var package in sortedPackages)
                    {
                        string installPath = localRepository.PathResolver.GetInstallPath(package);

                        AddPathToEnvironment(Path.Combine(installPath, "tools"));
                        Runspace.ExecuteScript(installPath, "tools\\init.ps1", package);
                    }
                }
                catch (Exception ex)
                {
                    // if execution of Init scripts fails, do not let it crash our console
                    ReportError(ex);

                    ExceptionHelper.WriteToActivityLog(ex);
                }
            }
        }

        private static void AddPathToEnvironment(string path)
        {
            if (Directory.Exists(path))
            {
                string environmentPath = Environment.GetEnvironmentVariable("path", EnvironmentVariableTarget.Process);
                environmentPath = environmentPath + ";" + path;
                Environment.SetEnvironmentVariable("path", environmentPath, EnvironmentVariableTarget.Process);
            }
        }

        protected abstract bool ExecuteHost(string fullCommand, string command, params object[] inputs);

        public bool Execute(IConsole console, string command, params object[] inputs)
        {
            if (console == null)
            {
                throw new ArgumentNullException("console");
            }

            if (command == null)
            {
                throw new ArgumentNullException("command");
            }

            NuGetEventTrigger.Instance.TriggerEvent(NuGetEvent.PackageManagerConsoleCommandExecutionBegin);
            ActiveConsole = console;
            
            string fullCommand;
            if (ComplexCommand.AddLine(command, out fullCommand) && !string.IsNullOrEmpty(fullCommand))
            {
                return ExecuteHost(fullCommand, command, inputs);
            }

            return false; // constructing multi-line command
        }

        protected static void OnExecuteCommandEnd()
        {
            NuGetEventTrigger.Instance.TriggerEvent(NuGetEvent.PackageManagerConsoleCommandExecutionEnd);
        }

        public void Abort()
        {
            if (ExecutingPipeline != null)
            {
                ExecutingPipeline.StopAsync();
            }
            ComplexCommand.Clear();
        }

        protected void SetSyncModeOnHost(bool isSync)
        {
            if (_nugetHost != null)
            {
                PSPropertyInfo property = _nugetHost.PrivateData.Properties["IsSyncMode"];
                if (property == null)
                {
                    property = new PSNoteProperty("IsSyncMode", isSync);
                    _nugetHost.PrivateData.Properties.Add(property);
                }
                else
                {
                    property.Value = isSync;
                }
            }
        }

        public void SetDefaultRunspace()
        {
            Runspace.MakeDefault();
        }

        private void DisplayDisclaimerAndHelpText()
        {
            WriteLine(VsResources.Console_DisclaimerText);
            WriteLine();

            WriteLine(String.Format(CultureInfo.CurrentCulture, Resources.PowerShellHostTitle, _nugetHost.Version.ToString()));
            WriteLine();

            WriteLine(VsResources.Console_HelpText);
            WriteLine();
        }

        protected void ReportError(ErrorRecord record)
        {
            WriteErrorLine(Runspace.ExtractErrorFromErrorRecord(record));
        }

        protected void ReportError(Exception exception)
        {
            exception = ExceptionUtility.Unwrap(exception);
            WriteErrorLine(exception.Message);
        }

        private void WriteErrorLine(string message)
        {
            if (ActiveConsole != null)
            {
                ActiveConsole.Write(message + Environment.NewLine, System.Windows.Media.Colors.Red, null);
            }
        }

        private void WriteLine(string message = "")
        {
            if (ActiveConsole != null)
            {
                ActiveConsole.WriteLine(message);
            }
        }

        public string ActivePackageSource
        {
            get
            {
                var activePackageSource = _packageSourceProvider.ActivePackageSource;
                if (activePackageSource.IsAggregate())
                {
                    // Starting from 2.7, we will not show the All option if there's only one package source.
                    // Hence, if All is the active package source in that case, we set the sole package source as active,
                    // and save it to settings
                    PackageSource[] packageSources = _packageSourceProvider.GetEnabledPackageSourcesWithAggregate().ToArray();
                    if (packageSources.Length == 1)
                    {
                        _packageSourceProvider.ActivePackageSource = packageSources[0];
                        return packageSources[0].Name;
                    }
                }

                return activePackageSource == null ? null : activePackageSource.Name;
            }
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    throw new ArgumentNullException("value");
                }

                _packageSourceProvider.ActivePackageSource =
                    _packageSourceProvider.GetEnabledPackageSourcesWithAggregate().FirstOrDefault(
                        ps => ps.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
            }
        }

        public string[] GetPackageSources()
        {
            // Starting NuGet 3.0 RC, AggregateSource will not be displayed in the Package source dropdown box of PowerShell console.
            return _packageSourceProvider.GetEnabledPackageSources().Select(ps => ps.Name).ToArray();
        }

        public string DefaultProject
        {
            get
            {
                Debug.Assert(_solutionManager != null);
                if (_solutionManager.DefaultProject == null)
                {
                    return null;
                }

                return _solutionManager.DefaultProject.GetDisplayName(_solutionManager);
            }
        }

        public void SetDefaultProjectIndex(int selectedIndex)
        {
            Debug.Assert(_solutionManager != null);

            if (_projectSafeNames != null && selectedIndex >= 0 && selectedIndex < _projectSafeNames.Length)
            {
                _solutionManager.DefaultProjectName = _projectSafeNames[selectedIndex];
            }
            else
            {
                _solutionManager.DefaultProjectName = null;
            }
        }

        public string[] GetAvailableProjects()
        {
            Debug.Assert(_solutionManager != null);

            var allProjects = _solutionManager.GetProjects();
            _projectSafeNames = allProjects.Select(_solutionManager.GetProjectSafeName).ToArray();
            var displayNames = allProjects.Select(p => p.GetDisplayName(_solutionManager)).ToArray();
            Array.Sort(displayNames, _projectSafeNames, StringComparer.CurrentCultureIgnoreCase);
            return displayNames;
        }

        #region ITabExpansion
        public string[] GetExpansions(string line, string lastWord)
        {
            var query = from s in Runspace.Invoke(
                            @"$__pc_args=@();$input|%{$__pc_args+=$_};if(Test-Path Function:\TabExpansion2){(TabExpansion2 $__pc_args[0] $__pc_args[0].length).CompletionMatches|%{$_.CompletionText}}else{TabExpansion $__pc_args[0] $__pc_args[1]};Remove-Variable __pc_args -Scope 0;",
                            new string[] { line, lastWord },
                            outputResults: false)
                        select (s == null ? null : s.ToString());
            return query.ToArray();
        }
        #endregion

        #region IPathExpansion
        public SimpleExpansion GetPathExpansions(string line)
        {
            PSObject expansion = Runspace.Invoke(
                "$input|%{$__pc_args=$_}; _TabExpansionPath $__pc_args; Remove-Variable __pc_args -Scope 0",
                new object[] { line },
                outputResults: false).FirstOrDefault();
            if (expansion != null)
            {
                int replaceStart = (int)expansion.Properties["ReplaceStart"].Value;
                IList<string> paths = ((IEnumerable<object>)expansion.Properties["Paths"].Value).Select(o => o.ToString()).ToList();
                return new SimpleExpansion(replaceStart, line.Length - replaceStart, paths);
            }

            return null;
        }
        #endregion

        #region IDisposable
        public void Dispose()
        {
            if (_runspace != null)
            {
                _runspace.Dispose();
            }
        }
        #endregion
    }
}