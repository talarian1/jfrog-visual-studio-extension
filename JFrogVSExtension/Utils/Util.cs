﻿using JFrogVSExtension.Data;
using JFrogVSExtension.Logger;
using JFrogVSExtension.Xray;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JFrogVSExtension.Utils
{
    class Util
    {
        public readonly static string PREFIX = "nuget://";
        
        // This method will load the json to a List of objects. 
        // The Json retrieved from the output itself
        public static Projects LoadNugetProjects(String output)
        {
            // Reading the file as stream and changing to list of items.
            // The items are configured in another class
            Projects projects = JsonConvert.DeserializeObject<Projects>(output);
            return projects;
        }

        public static async Task<string> GetCLIOutputAsync(string command,string workingDir = "",bool configCommand=false, Dictionary<string,string> envVars= null)
        {
            var strAppPath = GetAssemblyLocalPathFrom(typeof(MainPanelCommand));
            var strFilePath = Path.Combine(strAppPath, "Resources");
            var pathToCli = Path.Combine(strFilePath, "jfrog.exe");
            await OutputLog.ShowMessageAsync("Path for the JFrog CLI: " + pathToCli);
            //Create process
            Process pProcess = new System.Diagnostics.Process();

            // strCommand is path and file name of command to run
            pProcess.StartInfo.FileName = pathToCli;

            // strCommandParameters are parameters to pass to program
            // Here we will run the nuget command for the cli
            pProcess.StartInfo.Arguments = command;
            // Avoid printing commands with credentials
            var commandString = configCommand ? "config command" : command;

            pProcess.StartInfo.UseShellExecute = false;
            pProcess.StartInfo.CreateNoWindow = true;
            // Set output of program to be written to process output stream
            pProcess.StartInfo.RedirectStandardOutput = true;
            pProcess.StartInfo.RedirectStandardError = true;
            pProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            pProcess.StartInfo.WorkingDirectory = workingDir;
            if (envVars != null)
            {
                foreach (var envVar in envVars) 
                {
                    pProcess.StartInfo.EnvironmentVariables[envVar.Key] = envVar.Value;
                }
            }
            StringBuilder strOutput = new StringBuilder();
            StringBuilder error = new StringBuilder();

            // Saving the response from the CLI to a StringBuilder.
            using (AutoResetEvent outputWaitHandle = new AutoResetEvent(false))
            using (AutoResetEvent errorWaitHandle = new AutoResetEvent(false))
            {
                // Get program output
                // The json returned from the CLI
                pProcess.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data == null)
                    {
                        outputWaitHandle.Set();
                    }
                    else
                    {
                        strOutput.AppendLine(e.Data);
                    }
                };
                pProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data == null)
                    {
                        errorWaitHandle.Set();
                    }
                    else
                    {
                        error.AppendLine(e.Data);
                    }
                };

                // Start the process
                pProcess.Start();
                pProcess.BeginOutputReadLine();
                pProcess.BeginErrorReadLine();
                pProcess.WaitForExit();

                // Wait for the entire output to be written
                if (outputWaitHandle.WaitOne(1) &&
                       errorWaitHandle.WaitOne(1))
                {
                    // Process completed. Check process.ExitCode here.
                    if (pProcess.ExitCode != 0)
                    {
                        string message = $"Failed to get CLI output for {commandString}. Exit code: {pProcess.ExitCode} Returned error:{error}";
                        throw new IOException(message);
                    }
                    if (!string.IsNullOrEmpty(error.ToString()))
                    {
                        await OutputLog.ShowMessageAsync(error.ToString());
                    }
                    // Returning the output from the CLI that is the json itself.
                    await OutputLog.ShowMessageAsync($"JFrog CLI {commandString} finished successfully");
                    return strOutput.ToString();
                }
                else
                {
                    // Timed out.
                    await OutputLog.ShowMessageAsync("Process timeout");
                    throw new IOException($"Process timeout,  {pathToCli} {commandString}");
                }
            }
        }

        private static string GetAssemblyLocalPathFrom(Type type)
        {
            string codebase = type.Assembly.CodeBase;
            var uri = new Uri(codebase, UriKind.Absolute);

            return Path.GetDirectoryName(uri.LocalPath);
        }

        public static Component ParseDependencies(Dependency dep, Dictionary<string, Artifact> artifactsMap, DataService dataService)
        {
            Component comp = new Component(dep.id);
            Severity topSeverity = Severity.Normal;
            if (artifactsMap.ContainsKey(dep.id))
            {
                Artifact artifact = artifactsMap[dep.id];
                SetComponentIssuesAndLicenses(comp, artifact);

                if (artifact.Issues != null && artifact.Issues.Count > 0)
                {
                    topSeverity = GetTopSeverityFromIssues(artifact.Issues);
                }
            }

            var projectDependencies = new List<string>();
            if (dep.dependencies != null && dep.dependencies.Length > 0)
            {
                foreach (Dependency dependency in dep.dependencies)
                {
                    // Let's get the component information of the dependency. 
                    Component depComponent = ParseDependencies(dependency, artifactsMap, dataService);
                    if (!dataService.Severities.Contains(depComponent.TopSeverity))
                    {
                        continue;
                    }

                    topSeverity = GetTopSeverity(topSeverity, depComponent.TopSeverity);
                    if (depComponent.Issues != null && depComponent.Issues.Count > 0 && comp.Issues != null && comp.Issues.Count > 0)
                    {
                        // Means that the component already has some issues. 
                        // Need to check that this is a new issue that we are adding.
                        foreach (Issue issue in depComponent.Issues)
                        {
                            if (!comp.Issues.Contains(issue))
                            {
                                comp.Issues.Add(issue);
                            }
                        }
                    }
                    else
                    {
                        if (depComponent.Issues != null)
                        {
                            comp.Issues.AddRange(depComponent.Issues);
                        }
                    }

                    if (!dataService.getComponents().ContainsKey(depComponent.Key))
                    {
                        dataService.getComponents().Add(depComponent.Key, depComponent);
                    }
                    else
                    {
                        updateDataServiceWithMissingDependencies(depComponent, dataService);
                    }

                    projectDependencies.Add(dependency.id);
                }
            }
            comp.Dependencies = projectDependencies;
            comp.TopSeverity = topSeverity;
           
            return comp;
        }

        // Make sure that all of the component's dependencies are added to the DataService.
        // This scenario might happen as a package may appear in the dependencies-tree numerous times with different dependencies.
        // This is due to a possible situation of multiple subprojects under the same solution,
        // where the same package appears in several projects, but for some projects, some dependencies are missing in the packages.config.
        private static void updateDataServiceWithMissingDependencies(Component component, DataService dataService)
        {
            Component componentInDataService = dataService.getComponent(component.Key);
            if (component.Dependencies == null || componentInDataService == null)
            {
                return;
            }

            if (componentInDataService.Dependencies == null)
            {
                componentInDataService.Dependencies = new List<string>();
            }

            foreach (string dependencyString in component.Dependencies)
            {
                if (!componentInDataService.Dependencies.Contains(dependencyString))
                {
                    componentInDataService.Dependencies.Add(dependencyString);
                }
            }
        }

        private static void SetComponentIssuesAndLicenses(Component comp, Artifact artifact)
        {
            if (artifact.Issues != null && artifact.Issues.Count > 0)
            {
                foreach (Issue issue in artifact.Issues)
                {
                    issue.Component = artifact.ArtifactId;
                }
            }
            comp.Issues = artifact.Issues;
            comp.Licenses = artifact.Licenses;
        }

        // Return Set of Components which are not contained in componentsCache.
        public static HashSet<Components> GetNoCachedComponents(Dependency[] dependencies, HashSet<Components> componentsCache)
        {
            HashSet<Components> ids = new HashSet<Components>();
            foreach (Dependency dependency in dependencies)
            {
                Components comp = new Components()
                {
                    component_id = PREFIX + dependency.id
                };

                if (!componentsCache.Contains(comp))
                {
                    ids.Add(comp);
                }

                // Discover comp's dependencies even if already exists in cache.
                // This is due to a possible situation of multiple subprojects under the same solution,
                // where the same package appears in several projects, but for some projects, some dependencies are missing in the packages.config.
                // In such case, the CLI outputs a dependencies-tree in which each project shows different dependencies for the package.
                if (dependency.dependencies != null && dependency.dependencies.Length > 0)
                {
                    HashSet<Components> internalIdS = GetNoCachedComponents(dependency.dependencies, componentsCache);
                    ids.UnionWith(internalIdS);
                }
            }
            return ids;
        }

        public static Severity GetTopSeverity(Severity topSeverityComp, Severity topSeverityDep)
        {
            int compID = JFrogMonikerSelector.GetSeverityID(topSeverityComp);
            int compIDDep = JFrogMonikerSelector.GetSeverityID(topSeverityDep);

            if (compID <= compIDDep)
            {
                return topSeverityComp;
            }
            return topSeverityDep;
        }

        internal static Severity GetTopSeverityFromIssues(List<Issue> issues)
        {
            Severity topSeverity = Severity.Unknown;
            foreach (Issue issue in issues)
            {
                topSeverity = GetTopSeverity(topSeverity, issue.Severity);
            }
            return topSeverity;
        }
    }

    public class Artifacts
    {
        internal IEnumerable<Artifact> artifact;

        public List<Artifact> artifacts { get; set; } = new List<Artifact>();
    }

    public class NugetProject
    {
        public string name;
        public Dependency[] dependencies;
    }

    public class Dependency
    {
        public string id;
        public string sha1;
        public string md5;
        public Dependency[] dependencies;
    }

    public class Projects
    {
        public NugetProject[] projects;
    }

    public class Components
    {
        public string sha1 = "";
        public string component_id = "";

        public Components()
        {
        }

        public Components(String sha1, String component_id)
        {
            this.sha1 = sha1;
            this.component_id = component_id;
        }

        public override int GetHashCode()
        {
            return sha1.GetHashCode() + component_id.GetHashCode();
        }

        public override bool Equals(object obj)
        {
            if (!(obj is Components))
            {
                return false;
            }
            var comp = (Components)obj;

            return (this.sha1.Equals(comp.sha1) && this.component_id.Equals(comp.component_id));
        }
    }
}
