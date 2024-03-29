﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Xml.Linq;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using NuGet.Common;

namespace NuGet.Commands
{
    [SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling")]
    internal class ProjectFactory : IPropertyProvider
    {
        private readonly Project _project;
        private ILogger _logger;
        private ISettings _settings;

        // Files we want to always exclude from the resulting package
        private static readonly HashSet<string> _excludeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            Constants.PackageReferenceFile,
            "Web.Debug.config",
            "Web.Release.config"
        };

        private readonly Dictionary<string, string> _properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Packaging folders
        private const string ContentFolder = "content";
        private const string ReferenceFolder = "lib";
        private const string ToolsFolder = "tools";
        private const string SourcesFolder = "src";

        // Common item types
        private const string SourcesItemType = "Compile";
        private const string ContentItemType = "Content";
        private const string ProjectReferenceItemType = "ProjectReference";
        private const string NuGetConfig = "nuget.config";
        private const string PackagesFolder = "packages";
        private const string TransformFileExtension = ".transform";

        [Import]
        public IMachineWideSettings MachineWideSettings { get; set; }

        public ProjectFactory(string path, IDictionary<string, string> projectProperties)
            : this(new Project(path, projectProperties, null))
        {
        }

        public ProjectFactory(Project project)
        {
            _project = project;
            ProjectProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddSolutionDir();
            _settings = null;

            // Get the target framework of the project
            string targetFrameworkMoniker = _project.GetPropertyValue("TargetFrameworkMoniker");
            if (!String.IsNullOrEmpty(targetFrameworkMoniker))
            {
                TargetFramework = new FrameworkName(targetFrameworkMoniker);
            }
        }

        private ISettings DefaultSettings
        {
            get
            {
                if (null == _settings)
                {
                    _settings = Settings.LoadDefaultSettings(
                        new PhysicalFileSystem(_project.DirectoryPath),
                        null,
                        MachineWideSettings);
                }
                return _settings;
            }
        }

        private string TargetPath
        {
            get;
            set;
        }

        private FrameworkName TargetFramework
        {
            get;
            set;
        }

        public bool IncludeSymbols { get; set; }

        public bool ExcludeSourceCode { get; set; }

        public bool IncludeReferencedProjects { get; set; }

        public bool Build { get; set; }

        public Dictionary<string, string> ProjectProperties { get; private set; }

        public bool IsTool { get; set; }

        public ILogger Logger
        {
            get
            {
                return _logger ?? NullLogger.Instance;
            }
            set
            {
                _logger = value;
            }
        }

        public string BaseTargetPath { get; set; }
        public string SolutionName { get; set; }

        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "We want to continue regardless of any error we encounter extracting metadata.")]
        public PackageBuilder CreateBuilder(string basePath)
        {
            BuildProject();

            Logger.Log(MessageLevel.Info, LocalizedResourceManager.GetString("PackagingFilesFromOutputPath"), Path.GetDirectoryName(TargetPath));

            var builder = new PackageBuilder();

            try
            {
                // Populate the package builder with initial metadata from the assembly/exe
                AssemblyMetadataExtractor.ExtractMetadata(builder, TargetPath);
            }
            catch (Exception ex)
            {
                Logger.Log(MessageLevel.Warning, LocalizedResourceManager.GetString("UnableToExtractAssemblyMetadata"), Path.GetFileName(TargetPath));
                IConsole console = Logger as IConsole;
                if (console != null && console.Verbosity == Verbosity.Detailed)
                {
                    console.Log(MessageLevel.Warning, ex.ToString());
                }

                ExtractMetadataFromProject(builder);
            }

            var projectAuthor = InitializeProperties(builder);

            // Only override properties from assembly extracted metadata if they haven't 
            // been specified also at construction time for the factory (that is, 
            // console properties always take precedence.
            foreach (var key in builder.Properties.Keys)
            {
                if (!_properties.ContainsKey(key) && 
                    !ProjectProperties.ContainsKey(key))
                    _properties.Add(key, builder.Properties[key]);
            }

            // If the package contains a nuspec file then use it for metadata
            Manifest manifest = ProcessNuspec(builder, basePath);

            // Remove the extra author
            if (builder.Authors.Count > 1)
            {
                builder.Authors.Remove(projectAuthor);
            }

            // Add output files
            ApplyAction(p => p.AddOutputFiles(builder));

            // if there is a .nuspec file, only add content files if the <files /> element is not empty.
            if (manifest == null || manifest.Files == null || manifest.Files.Count > 0)
            {
                // Add content files
                ApplyAction(p => p.AddFiles(builder, ContentItemType, ContentFolder));
            }

            // Add sources if this is a symbol package
            if (IncludeSymbols && !ExcludeSourceCode)
            {
                ApplyAction(p => p.AddFiles(builder, SourcesItemType, SourcesFolder));
            }

            ProcessDependencies(builder);

            // Set defaults if some required fields are missing
            if (String.IsNullOrEmpty(builder.Description))
            {
                builder.Description = "Description";
                Logger.Log(MessageLevel.Warning, LocalizedResourceManager.GetString("Warning_UnspecifiedField"), "Description", "Description");
            }

            if (!builder.Authors.Any())
            {
                builder.Authors.Add(Environment.UserName);
                Logger.Log(MessageLevel.Warning, LocalizedResourceManager.GetString("Warning_UnspecifiedField"), "Author", Environment.UserName);
            }

            return builder;
        }

        internal string InitializeProperties(IPackageMetadata metadata)
        {
            // Set the properties that were resolved from the assembly/project so they can be
            // resolved by name if the nuspec contains tokens
            _properties.Clear();

            // Allow Id to be overriden by cmd line properties
            if (ProjectProperties.ContainsKey("Id"))
                _properties.Add("Id", ProjectProperties["Id"]);
            else
                _properties.Add("Id", metadata.Id);
            
            _properties.Add("Version", metadata.Version.ToString());

            if (!String.IsNullOrEmpty(metadata.Title))
            {
                _properties.Add("Title", metadata.Title);
            }

            if (!String.IsNullOrEmpty(metadata.Description))
            {
                _properties.Add("Description", metadata.Description);
            }

            if (!String.IsNullOrEmpty(metadata.Copyright))
            {
                _properties.Add("Copyright", metadata.Copyright);
            }

            string projectAuthor = metadata.Authors.FirstOrDefault();
            if (!String.IsNullOrEmpty(projectAuthor))
            {
                _properties.Add("Author", projectAuthor);
            }
            return projectAuthor;
        }

        dynamic IPropertyProvider.GetPropertyValue(string propertyName)
        {
            string value;
            if (!_properties.TryGetValue(propertyName, out value) && 
                !ProjectProperties.TryGetValue(propertyName, out value))
            {
                ProjectProperty property = _project.GetProperty(propertyName);
                if (property != null)
                {
                    value = property.EvaluatedValue;
                }
            }

            return value;
        }

        private void BuildProject()
        {
            if (Build)
            {
                if (TargetFramework != null)
                {
                    Logger.Log(
                        MessageLevel.Info, 
                        LocalizedResourceManager.GetString("BuildingProjectTargetingFramework"), 
                        _project.FullPath,
                        TargetFramework);
                }

                using (var projectCollection = new ProjectCollection(ToolsetDefinitionLocations.Registry | ToolsetDefinitionLocations.ConfigurationFile))
                {
                    BuildRequestData requestData = new BuildRequestData(_project.FullPath, ProjectProperties, _project.ToolsVersion, new string[0], null);
                    var parameters = new BuildParameters(projectCollection)
                                     {
                                         Loggers = new[] { new ConsoleLogger { Verbosity = LoggerVerbosity.Quiet }},
                                         NodeExeLocation = typeof(ProjectFactory).Assembly.Location,
                                         ToolsetDefinitionLocations = projectCollection.ToolsetLocations
                                     };
                    BuildResult result = BuildManager.DefaultBuildManager.Build(parameters, requestData);
                    // Build the project so that the outputs are created
                    if (result.OverallResult == BuildResultCode.Failure)
                    {
                        // If the build fails, report the error
                        throw new CommandLineException(LocalizedResourceManager.GetString("FailedToBuildProject"), Path.GetFileName(_project.FullPath));
                    }

                    TargetPath = ResolveTargetPath(result);
                }
            }
            else
            {
                // If BaseTargetPath property is provided, then use its value for resolving the TargetPath
                // BaseTargetPath property only makes sense when Build==false.
                TargetPath = !string.IsNullOrWhiteSpace(BaseTargetPath)
                                 ? ResolveTargetPathByBaseTargetPath()
                                 : ResolveTargetPath();

                // Make if the target path doesn't exist, fail
                if (!File.Exists(TargetPath))
                {
                    throw new CommandLineException(LocalizedResourceManager.GetString("UnableToFindBuildOutput"), TargetPath);
                }
            }
        }

        /// <summary>
        /// Resolves the TargetPath for a project built by TFS Team Build.
        /// Format is:
        /// {BaseTargetPath}\bin\[{Platform}]\[{Configuration}]\[{Solution}]\{Project}\{ProjectAssembly}
        /// </summary>
        /// <returns></returns>
        private string ResolveTargetPathByBaseTargetPath()
        {
            // The original resolution method is called to update the _project as normal.
            ResolveTargetPath();

            string targetPath = BaseTargetPath;

            // Add the platform and configuration specific subfolders,
            // e.g. "bin\x64\Release"
            // Platform and Configuration subfolders are included only if explicit
            // properties are provided to the PackCommand,
            // e.g. -Properties Platform=x64 -Properties Configuration=Release
            // On TFS Team Build, platform and configuration specific subfolders are included
            // when more than one "Configuration to Build" is selected.
            targetPath = Path.Combine(targetPath, _project.GetPropertyValue("OutputPath"));

            // Add the solution specific subfolder,
            // e.g. "MySolution"
            if (!string.IsNullOrWhiteSpace(SolutionName))
            {
                // On TFS Team Build, solution specific subfolder is included when
                // "Solution Specific Build Outputs" is enabled.
                targetPath = Path.Combine(targetPath, SolutionName);
            }

            // Add the project specific subfolder,
            // e.g. "MyProject"
            // Project specific subfolders are produced by TFS Team Build when
            // MSBuild agrument /p:GenerateProjectSpecificOutputFolder=true is provided.
            targetPath = Path.Combine(targetPath, _project.GetPropertyValue("MSBuildProjectName"));

            // Add the target assembly filename,
            // e.g. "MyProject.dll"
            targetPath = Path.Combine(targetPath, _project.GetPropertyValue("TargetFileName"));

            return targetPath;
        }

        private string ResolveTargetPath()
        {
            // Set the project properties
            foreach (var property in ProjectProperties)
            {
                var existingProperty = _project.GetProperty(property.Key);
                if (existingProperty == null || !IsGlobalProperty(existingProperty))
                {
                    // Only set the property if it's not already defined as a global property
                    // (which those passed in via the ctor are) as trying to set global properties
                    // with this method throws.
                    _project.SetProperty(property.Key, property.Value);
                }
            }

            // Re-evaluate the project so that the new property values are applied
            _project.ReevaluateIfNecessary();

            // Return the new target path
            return _project.GetPropertyValue("TargetPath");
        }

        private static bool IsGlobalProperty(ProjectProperty projectProperty)
        {
            // This property isn't available on xbuild (mono)
            var property = typeof(ProjectProperty).GetProperty("IsGlobalProperty", BindingFlags.Public | BindingFlags.Instance);
            if(property != null) 
            {
                return (bool)property.GetValue(projectProperty, null);
            }

            // REVIEW: Maybe there's something better we can do on mono
            // Just return false if the property isn't there
            return false;
        }

        private string ResolveTargetPath(BuildResult result)
        {
            string targetPath = null;
            TargetResult targetResult;
            if (result.ResultsByTarget.TryGetValue("Build", out targetResult))
            {
                if (targetResult.Items.Any())
                {
                    targetPath = targetResult.Items.First().ItemSpec;
                }
            }

            return targetPath ?? ResolveTargetPath();
        }

        private void ExtractMetadataFromProject(PackageBuilder builder)
        {
            builder.Id = builder.Id ??
                        _project.GetPropertyValue("AssemblyName") ??
                        Path.GetFileNameWithoutExtension(_project.FullPath);

            string version = _project.GetPropertyValue("Version");
            builder.Version = builder.Version ??
                              SemanticVersion.ParseOptionalVersion(version) ??
                              new SemanticVersion("1.0");
        }

        private static IEnumerable<string> GetFiles(string path, string fileNameWithoutExtension, HashSet<string> allowedExtensions)
        {
            return allowedExtensions.Select(extension => Directory.GetFiles(path, fileNameWithoutExtension + extension)).SelectMany(a => a);
        }

        private void ApplyAction(Action<ProjectFactory> action)
        {
            if (IncludeReferencedProjects)
            {
                RecursivelyApply(action);
            }
            else
            {
                action(this);
            }
        }

        /// <summary>
        /// Recursively execute the specified action on the current project and
        /// projects referenced by the current project.
        /// </summary>
        /// <param name="action">The action to be executed.</param>
        private void RecursivelyApply(Action<ProjectFactory> action)
        {
            using (var projectCollection = new ProjectCollection())
            {
                RecursivelyApply(action, projectCollection);
            }
        }

        /// <summary>
        /// Recursively execute the specified action on the current project and
        /// projects referenced by the current project.
        /// </summary>
        /// <param name="action">The action to be executed.</param>
        /// <param name="alreadyAppliedProjects">The collection of projects that have been processed.
        /// It is used to avoid processing the same project more than once.</param>
        private void RecursivelyApply(Action<ProjectFactory> action, ProjectCollection alreadyAppliedProjects)
        {
            action(this);
            foreach (var item in _project.GetItems(ProjectReferenceItemType))
            {
                string fullPath = item.GetMetadataValue("FullPath");
                if (!string.IsNullOrEmpty(fullPath) && 
                    !NuspecFileExists(fullPath) &&
                    alreadyAppliedProjects.GetLoadedProjects(fullPath).IsEmpty())
                {
                    var project = new Project(
                        fullPath, 
                        globalProperties: null, 
                        toolsVersion: null, 
                        projectCollection: alreadyAppliedProjects);
                    var referencedProject = new ProjectFactory(project);
                    referencedProject.Logger = _logger;
                    referencedProject.IncludeSymbols = IncludeSymbols;
                    referencedProject.Build = Build;
                    referencedProject.IncludeReferencedProjects = IncludeReferencedProjects;
                    referencedProject.ProjectProperties = ProjectProperties;
                    referencedProject.TargetFramework = TargetFramework;
                    referencedProject.BaseTargetPath = BaseTargetPath;
                    referencedProject.SolutionName = SolutionName;
                    referencedProject.BuildProject();
                    referencedProject.RecursivelyApply(action, alreadyAppliedProjects);
                }
            }
        }

        /// <summary>
        /// Returns whether a project file has a corresponding nuspec file.
        /// </summary>
        /// <param name="projectFileFullName">The name of the project file.</param>
        /// <returns>True if there is a corresponding nuspec file.</returns>
        private static bool NuspecFileExists(string projectFileFullName)
        {
            var nuspecFile = Path.ChangeExtension(projectFileFullName, Constants.ManifestExtension);
            return File.Exists(nuspecFile);
        }

        /// <summary>
        /// Adds referenced projects that have corresponding nuspec files as dependencies.
        /// </summary>
        /// <param name="dependencies">The dependencies collection where the new dependencies
        /// are added into.</param>
        private void AddProjectReferenceDependencies(Dictionary<string, PackageDependency> dependencies)
        {
            var processedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var projectsToProcess = new Queue<Project>();

            using (var projectCollection = new ProjectCollection())
            {
                projectsToProcess.Enqueue(_project);
                while (projectsToProcess.Count > 0)
                {
                    var project = projectsToProcess.Dequeue();
                    processedProjects.Add(project.FullPath);

                    foreach (var projectReference in project.GetItems(ProjectReferenceItemType))
                    {
                        string fullPath = projectReference.GetMetadataValue("FullPath");
                        if (string.IsNullOrEmpty(fullPath) ||
                            processedProjects.Contains(fullPath))
                        {
                            continue;
                        }

                        var loadedProjects = projectCollection.GetLoadedProjects(fullPath);
                        var referencedProject = loadedProjects.Count > 0 ?
                            loadedProjects.First() :
                            new Project(
                                fullPath,
                                globalProperties: project.GlobalProperties,
                                toolsVersion: null,
                                projectCollection: projectCollection);

                        if (NuspecFileExists(fullPath))
                        {
                            var dependency = CreateDependencyFromProject(referencedProject);
                            dependencies[dependency.Id] = dependency;
                        }
                        else
                        {
                            projectsToProcess.Enqueue(referencedProject);
                        }
                    }
                }
            }
        }

        // Creates a package dependency from the given project, which has a corresponding
        // nuspec file.
        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "We want to continue regardless of any error we encounter extracting metadata.")]
        private PackageDependency CreateDependencyFromProject(Project project)
        {
            try
            {
                var projectFactory = new ProjectFactory(project);
                projectFactory.Build = Build;
                projectFactory.ProjectProperties = ProjectProperties;
                projectFactory.BaseTargetPath = BaseTargetPath;
                projectFactory.SolutionName = SolutionName;
                projectFactory.BuildProject();
                var builder = new PackageBuilder();
                try
                {
                    AssemblyMetadataExtractor.ExtractMetadata(builder, projectFactory.TargetPath);
                }
                catch
                {
                    projectFactory.ExtractMetadataFromProject(builder);
                }

                projectFactory.InitializeProperties(builder);
                projectFactory.ProcessNuspec(builder, null);
                return new PackageDependency(
                    builder.Id,
                    VersionUtility.ParseVersionSpec(builder.Version.ToString()));
            }
            catch (Exception ex)
            {
                var message = string.Format(
                    CultureInfo.InvariantCulture,
                    LocalizedResourceManager.GetString("Error_ProcessingNuspecFile"),
                    project.FullPath,
                    ex.Message);
                throw new CommandLineException(message, ex);
            }
        }

        private void AddOutputFiles(PackageBuilder builder)
        {
            // Get the target framework of the project
            FrameworkName targetFramework = TargetFramework;

            // Get the target file path
            string targetPath = TargetPath;

            // List of extensions to allow in the output path
            var allowedOutputExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                ".dll",
                ".exe",
                ".xml",
                ".winmd"
            };

            if (IncludeSymbols)
            {
                // Include pdbs for symbol packages
                allowedOutputExtensions.Add(".pdb");
            }

            string projectOutputDirectory = Path.GetDirectoryName(targetPath);

            string targetFileName = Path.GetFileNameWithoutExtension(targetPath);

            // By default we add all files in the project's output directory
            foreach (var file in GetFiles(projectOutputDirectory, targetFileName, allowedOutputExtensions))
            {
                {
                    string extension = Path.GetExtension(file);

                    // Only look at files we care about
                    if (!allowedOutputExtensions.Contains(extension))
                    {
                        continue;
                    }

                    string targetFolder;

                    if (IsTool)
                    {
                        targetFolder = ToolsFolder;
                    }
                    else
                    {
                        if (targetFramework == null)
                        {
                            targetFolder = ReferenceFolder;
                        }
                        else
                        {
                            targetFolder = Path.Combine(ReferenceFolder, VersionUtility.GetShortFrameworkName(targetFramework));
                        }
                    }
                    var packageFile = new PhysicalPackageFile
                    {
                        SourcePath = file,
                        TargetPath = Path.Combine(targetFolder, Path.GetFileName(file))
                    };
                    AddFileToBuilder(builder, packageFile);
                }
            }
        }

        private void ProcessDependencies(PackageBuilder builder)
        {
            // get all packages and dependencies, including the ones in project references
            var packagesAndDependencies = new Dictionary<String, Tuple<IPackage, PackageDependency>>();
            ApplyAction(p => p.AddDependencies(packagesAndDependencies));

            // list of all dependency packages
            var packages = packagesAndDependencies.Values.Select(t => t.Item1).ToList();

            // Add the transform file to the package builder
            ProcessTransformFiles(builder, packages.SelectMany(GetTransformFiles));

            var dependencies = builder.GetCompatiblePackageDependencies(targetFramework: null)
                                      .ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);

            // Reduce the set of packages we want to include as dependencies to the minimal set.
            // Normally, packages.config has the full closure included, we only add top level
            // packages, i.e. packages with in-degree 0
            foreach (var package in GetMinimumSet(packages))
            {
                // Don't add duplicate dependencies
                if (dependencies.ContainsKey(package.Id))
                {
                    continue;
                }

                var dependency = packagesAndDependencies[package.Id].Item2;
                dependencies[dependency.Id] = dependency;
            }

            if (IncludeReferencedProjects)
            {
                AddProjectReferenceDependencies(dependencies);
            }

            // TO FIX: when we persist the target framework into packages.config file, 
            // we need to pull that info into building the PackageDependencySet object
            builder.DependencySets.Clear();
            builder.DependencySets.Add(new PackageDependencySet(null, dependencies.Values));
        }

        private void AddDependencies(Dictionary<String,Tuple<IPackage,PackageDependency>> packagesAndDependencies)
        {
            var file = PackageReferenceFile.CreateFromProject(_project.FullPath);
            if (!File.Exists(file.FullPath))
            {
                return;
            }
            Logger.Log(MessageLevel.Info, LocalizedResourceManager.GetString("UsingPackagesConfigForDependencies"));

            // Get the solution repository
            IPackageRepository repository = GetPackagesRepository();

            // Collect all packages
            IDictionary<PackageName, PackageReference> packageReferences = 
                file.GetPackageReferences()
                .Where(r => !r.IsDevelopmentDependency)
                .ToDictionary(r => new PackageName(r.Id, r.Version));
            // add all packages and create an associated dependency to the dictionary
            foreach (PackageReference reference in packageReferences.Values)
            {
                if (repository != null)
                {
                    IPackage package = repository.FindPackage(reference.Id, reference.Version);
                    if (package != null && !packagesAndDependencies.ContainsKey(package.Id))
                    {
                        IVersionSpec spec = GetVersionConstraint(packageReferences, package);
                        var dependency = new PackageDependency(package.Id, spec);
                        packagesAndDependencies.Add(package.Id, new Tuple<IPackage,PackageDependency>(package, dependency));
                    }
                }
            }
        }

        private static IVersionSpec GetVersionConstraint(IDictionary<PackageName, PackageReference> packageReferences, IPackage package)
        {
            IVersionSpec defaultVersionConstraint = VersionUtility.ParseVersionSpec(package.Version.ToString());

            PackageReference packageReference;
            var key = new PackageName(package.Id, package.Version);
            if (!packageReferences.TryGetValue(key, out packageReference))
            {
                return defaultVersionConstraint;
            }

            return packageReference.VersionConstraint ?? defaultVersionConstraint;
        }

        private IEnumerable<IPackage> GetMinimumSet(List<IPackage> packages)
        {
            return new Walker(packages, TargetFramework).GetMinimalSet();
        }

        private static void ProcessTransformFiles(PackageBuilder builder, IEnumerable<IPackageFile> transformFiles)
        {
            // Group transform by target file
            var transformGroups = transformFiles.GroupBy(file => RemoveExtension(file.Path), StringComparer.OrdinalIgnoreCase);
            var fileLookup = builder.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);

            foreach (var tranfromGroup in transformGroups)
            {
                IPackageFile file;
                if (fileLookup.TryGetValue(tranfromGroup.Key, out file))
                {
                    // Replace the original file with a file that removes the transforms
                    builder.Files.Remove(file);
                    builder.Files.Add(new ReverseTransformFormFile(file, tranfromGroup));
                }
            }
        }

        /// <summary>
        /// Removes a file extension keeping the full path intact
        /// </summary>
        private static string RemoveExtension(string path)
        {
            return Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path));
        }

        private IEnumerable<IPackageFile> GetTransformFiles(IPackage package)
        {
            return package.GetContentFiles().Where(IsTransformFile);
        }

        private static bool IsTransformFile(IPackageFile file)
        {
            return Path.GetExtension(file.Path).Equals(TransformFileExtension, StringComparison.OrdinalIgnoreCase);
        }

        private void AddSolutionDir()
        {
            // Add the solution dir to the list of properties
            string solutionDir = GetSolutionDir();

            // Add a path separator for Visual Studio macro compatibility
            solutionDir += Path.DirectorySeparatorChar;

            if (!String.IsNullOrEmpty(solutionDir))
            {
                if (this.ProjectProperties.ContainsKey("SolutionDir"))
                {
                    this.Logger.Log(MessageLevel.Warning, LocalizedResourceManager.GetString("Warning_DuplicatePropertyKey"), "SolutionDir");
                }

                ProjectProperties["SolutionDir"] = solutionDir;
            }
        }

        private string GetSolutionDir()
        {
            return ProjectHelper.GetSolutionDir(_project.DirectoryPath);
        }

        private IPackageRepository GetPackagesRepository()
        {
            string solutionDir = GetSolutionDir();
            string defaultValue = DefaultSettings.GetRepositoryPath();

            string target = null;
            if (!String.IsNullOrEmpty(solutionDir))
            {
                string configValue = GetPackagesPath(solutionDir);
                // solution dir exists, no default packages folder specified anywhere,
                // default to hardcoded "packages" folder under solution
                if (string.IsNullOrEmpty(configValue) && string.IsNullOrEmpty(defaultValue))
                {
                    configValue = PackagesFolder;
                }

                if (!string.IsNullOrEmpty(configValue))
                {
                    target = Path.Combine(solutionDir, configValue);
                }
            }

            if (string.IsNullOrEmpty(target))
            {
                target = defaultValue;
            }            

            if (!string.IsNullOrEmpty(target) && Directory.Exists(target))
            {
                return new SharedPackageRepository(target);
            }            

            return null;
        }

        private static string GetPackagesPath(string dir)
        {
            string configPath = Path.Combine(dir, NuGetConfig);

            try
            {
                // Support the hidden feature
                if (File.Exists(configPath))
                {
                    using (Stream stream = File.OpenRead(configPath))
                    {
                        // It's possible for the repositoryPath element to be missing in older versions of 
                        // a NuGet.config file.
                        var repositoryPathElement = XmlUtility.LoadSafe(stream).Root.Element("repositoryPath");
                        if (repositoryPathElement != null)
                        {
                            return repositoryPathElement.Value;
                        }
                    }
                }
            }
            catch (FileNotFoundException)
            {
            }

            return null;
        }

        private Manifest ProcessNuspec(PackageBuilder builder, string basePath)
        {
            string nuspecFile = GetNuspec();

            if (String.IsNullOrEmpty(nuspecFile))
            {
                return null;
            }

            Logger.Log(MessageLevel.Info, LocalizedResourceManager.GetString("UsingNuspecForMetadata"), Path.GetFileName(nuspecFile));

            using (Stream stream = File.OpenRead(nuspecFile))
            {
                // Don't validate the manifest since this might be a partial manifest
                // The bulk of the metadata might be coming from the project.
                Manifest manifest = Manifest.ReadFrom(stream, this, validateSchema: true);
                builder.Populate(manifest.Metadata);

                if (manifest.Files != null)
                {
                    basePath = String.IsNullOrEmpty(basePath) ? Path.GetDirectoryName(nuspecFile) : basePath;
                    builder.PopulateFiles(basePath, manifest.Files);
                }

                return manifest;
            }
        }

        private string GetNuspec()
        {
            return GetNuspecPaths().FirstOrDefault(File.Exists);
        }

        private IEnumerable<string> GetNuspecPaths()
        {
            // Check for a nuspec in the project file
            yield return GetContentOrNone(file => Path.GetExtension(file).Equals(Constants.ManifestExtension, StringComparison.OrdinalIgnoreCase));
            // Check for a nuspec named after the project
            yield return Path.Combine(_project.DirectoryPath, Path.GetFileNameWithoutExtension(_project.FullPath) + Constants.ManifestExtension);
        }

        private string GetContentOrNone(Func<string, bool> matcher)
        {
            return GetFiles("Content").Concat(GetFiles("None")).FirstOrDefault(matcher);
        }

        private IEnumerable<string> GetFiles(string itemType)
        {
            return _project.GetItems(itemType).Select(item => item.GetMetadataValue("FullPath"));
        }

        private void AddFiles(PackageBuilder builder, string itemType, string targetFolder)
        {
            // Skip files that are added by dependency packages 
            var references = PackageReferenceFile.CreateFromProject(_project.FullPath).GetPackageReferences();
            IPackageRepository repository = GetPackagesRepository();
            string projectName = Path.GetFileNameWithoutExtension(_project.FullPath);

            var contentFilesInDependencies = new List<IPackageFile>();
            if (references.Any() && repository != null)
            {
                contentFilesInDependencies = references.Select(reference => repository.FindPackage(reference.Id, reference.Version))
                                                       .Where(a => a != null)
                                                       .SelectMany(a => a.GetContentFiles())
                                                       .ToList();
            }

            // Get the content files from the project
            foreach (var item in _project.GetItems(itemType))
            {
                string fullPath = item.GetMetadataValue("FullPath");
                if (_excludeFiles.Contains(Path.GetFileName(fullPath)))
                {
                    continue;
                }

                string targetFilePath = GetTargetPath(item);

                if (!File.Exists(fullPath))
                {
                    Logger.Log(MessageLevel.Warning, LocalizedResourceManager.GetString("Warning_FileDoesNotExist"), targetFilePath);
                    continue;
                }

                // Skip target file paths containing msbuild variables since we do not offer a way to install files with variable paths.
                // These are show up in shared files found in universal apps.
                if (targetFilePath.IndexOf("$(MSBuild", StringComparison.OrdinalIgnoreCase) > -1)
                {
                    Logger.Log(MessageLevel.Warning, LocalizedResourceManager.GetString("Warning_UnresolvedFilePath"), targetFilePath);
                    continue;
                }

                // if IncludeReferencedProjects is true and we are adding source files,
                // add projectName as part of the target to avoid file conflicts.
                string targetPath = IncludeReferencedProjects && itemType == SourcesItemType ?               
                    Path.Combine(targetFolder, projectName, targetFilePath) :
                    Path.Combine(targetFolder, targetFilePath);

                // Check that file is added by dependency
                IPackageFile targetFile = contentFilesInDependencies.Find(a => a.Path.Equals(targetPath, StringComparison.OrdinalIgnoreCase));
                if (targetFile != null)
                {
                    // Compare contents as well
                    var isEqual = ContentEquals(targetFile, fullPath);
                    if (isEqual)
                    {
                        Logger.Log(MessageLevel.Info, LocalizedResourceManager.GetString("PackageCommandFileFromDependencyIsNotChanged"), targetFilePath);
                        continue;
                    }

                    Logger.Log(MessageLevel.Info, LocalizedResourceManager.GetString("PackageCommandFileFromDependencyIsChanged"), targetFilePath);
                }

                var packageFile = new PhysicalPackageFile
                {
                    SourcePath = fullPath,
                    TargetPath = targetPath
                };
                AddFileToBuilder(builder, packageFile);
            }
        }

        private void AddFileToBuilder(PackageBuilder builder, PhysicalPackageFile packageFile)
        {
            if (!builder.Files.Any(p => packageFile.Path.Equals(p.Path, StringComparison.OrdinalIgnoreCase)))
            {
                WriteDetail(LocalizedResourceManager.GetString("AddFileToPackage"), packageFile.SourcePath, packageFile.TargetPath);
                builder.Files.Add(packageFile);
            }
            else
            {
                _logger.Log(
                    MessageLevel.Warning,
                    LocalizedResourceManager.GetString("FileNotAddedToPackage"), 
                    packageFile.SourcePath, 
                    packageFile.TargetPath);
            }
        }

        private void WriteDetail(string format, params object[] args)
        {
            var console = _logger as NuGet.Common.Console;
            if (console != null && console.Verbosity == Verbosity.Detailed)
            {
                console.WriteLine(format, args);
            }
        }

        internal static bool ContentEquals(IPackageFile targetFile, string fullPath)
        {
            bool isEqual;
            using (var dependencyFileStream = targetFile.GetStream())
            using (var fileContentStream = File.OpenRead(fullPath))
            {
                isEqual = dependencyFileStream.ContentEquals(fileContentStream);
            }
            return isEqual;
        }

        private string GetTargetPath(ProjectItem item)
        {
            string path = item.UnevaluatedInclude;
            if (item.HasMetadata("Link"))
            {
                path = item.GetMetadataValue("Link");
            }
            return Normalize(path);
        }

        private string Normalize(string path)
        {
            string projectDirectoryPath = PathUtility.EnsureTrailingSlash(_project.DirectoryPath);
            string fullPath = PathUtility.GetAbsolutePath(projectDirectoryPath, path);

            // If the file is under the project root then remove the project root
            if (fullPath.StartsWith(projectDirectoryPath, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(_project.DirectoryPath.Length).TrimStart(Path.DirectorySeparatorChar);
            }

            // Otherwise the file is probably a shortcut so just take the file name
            return Path.GetFileName(fullPath);
        }

        private class Walker : PackageWalker
        {
            private readonly IPackageRepository _repository;
            private readonly List<IPackage> _packages;

            public Walker(List<IPackage> packages, FrameworkName targetFramework) :
                base(targetFramework)
            {
                _packages = packages;
                _repository = new ReadOnlyPackageRepository(packages.ToList());
            }

            protected override bool SkipDependencyResolveError
            {
                get
                {
                    // For the pack command, when don't need to throw if a dependency is missing 
                    // from a nuspec file.
                    return true;
                }
            }

            protected override IPackage ResolveDependency(PackageDependency dependency)
            {
                return DependencyResolveUtility.ResolveDependency(_repository, dependency, allowPrereleaseVersions: false, preferListedPackages: false);
            }

            protected override bool OnAfterResolveDependency(IPackage package, IPackage dependency)
            {
                _packages.Remove(dependency);
                return base.OnAfterResolveDependency(package, dependency);
            }

            public IEnumerable<IPackage> GetMinimalSet()
            {
                foreach (var package in _repository.GetPackages())
                {
                    Walk(package);
                }
                return _packages;
            }
        }

        private class ReverseTransformFormFile : IPackageFile
        {
            private readonly Lazy<Func<Stream>> _streamFactory;
            private readonly string _effectivePath;

            public ReverseTransformFormFile(IPackageFile file, IEnumerable<IPackageFile> transforms)
            {
                Path = file.Path + ".transform";
                _streamFactory = new Lazy<Func<Stream>>(() => ReverseTransform(file, transforms), isThreadSafe: false);
                TargetFramework = VersionUtility.ParseFrameworkNameFromFilePath(Path, out _effectivePath);
            }

            public string Path
            {
                get;
                private set;
            }

            public string EffectivePath
            {
                get
                {
                    return _effectivePath;
                }
            }

            public Stream GetStream()
            {
                return _streamFactory.Value();
            }

            [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "We need to return the MemoryStream for use.")]
            private static Func<Stream> ReverseTransform(IPackageFile file, IEnumerable<IPackageFile> transforms)
            {
                // Get the original
                XElement element = GetElement(file);

                // Remove all the transforms
                foreach (var transformFile in transforms)
                {
                    element.Except(GetElement(transformFile));
                }

                // Create the stream with the transformed content
                var ms = new MemoryStream();
                element.Save(ms);
                ms.Seek(0, SeekOrigin.Begin);
                byte[] buffer = ms.ToArray();
                return () => new MemoryStream(buffer);
            }

            private static XElement GetElement(IPackageFile file)
            {
                using (Stream stream = file.GetStream())
                {
                    return XElement.Load(stream);
                }
            }


            public FrameworkName TargetFramework
            {
                get;
                private set;
            }

            IEnumerable<FrameworkName> IFrameworkTargetable.SupportedFrameworks
            {
                get
                {
                    if (TargetFramework != null)
                    {
                        yield return TargetFramework;
                    }
                    yield break;
                }
            }
        }
    }
}
