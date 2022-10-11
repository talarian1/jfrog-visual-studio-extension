﻿using JFrogVSExtension.HttpClient;
using JFrogVSExtension.Logger;
using JFrogVSExtension.Utils;
using JFrogVSExtension.Utils.ScanManager;
using JFrogVSExtension.Xray;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace JFrogVSExtension.Data
{
    public class DataService
    {
        private static DataService instance;
        private Dictionary<string, Component> components;
        private HashSet<Components> componentsCache;
        private Artifacts artifacts = new Artifacts();
        public List<string> RootElements { get; private set; }
        public HashSet<Severity> Severities { get; set; }
        private DataService()
        {
            InitializeComponent();
        }
        public static DataService Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = new DataService();
                }
                return instance;
            }
        }

        public HashSet<Components> GetComponentsCache()
        {
            return componentsCache;
        }

        public Artifacts GetArtifacts()
        {
            return artifacts;
        }

        public void PopulateRootElements(Projects projects)
        {
            List<String> names = new List<String>();
            foreach (NugetProject project in projects.projects)
            {
                List<string> projectDependencies = new List<string>();
                Component comp = new Component()
                {
                    Key = project.name,
                    Group = project.name,
                    Issues = new List<Issue>()
                };
                Severity topSeverity = Severity.Unknown;
                if (project.dependencies != null && project.dependencies.Length > 0)
                {
                    foreach (Dependency dep in project.dependencies)
                    {
                        Component depComponent = GetComponent(dep);
                        if (Severities.Contains(depComponent.TopSeverity))
                        {
                            projectDependencies.Add(depComponent.Key);
                        } 
                        topSeverity = GetTopComponentSeverity(topSeverity, depComponent);
                        if (depComponent.Issues != null)
                        {
                            foreach (Issue issue in depComponent.Issues)
                            {
                                if (!comp.Issues.Contains(issue))
                                {
                                    comp.Issues.Add(issue);
                                }
                            }
                        }
                    }
                }

                comp.TopSeverity = topSeverity;
                comp.Dependencies = projectDependencies;
                names.Add(project.name);
                // Adding to the data service components the project itself
                if (!getComponents().ContainsKey(comp.Key))
                {
                    getComponents().Add(comp.Key, comp);                
                }
                else
                {
                    if (!getComponents().ContainsValue(comp))
                    {
                        //New value for the same key
                        getComponents().Remove(comp.Key);
                        getComponents().Add(comp.Key, comp);
                    }
                }
            }

            RootElements.Clear();
            foreach (String name in names)
            {
                RootElements.Add(name);
            }
        }

        private Component GetComponent(Dependency dep)
        {
            var artifactsMap = artifacts.artifacts.ToDictionary(x => x.ArtifactId, x => x);
            return Util.ParseDependencies(dep, artifactsMap, this);
        }

        // Parsing the dependencies and returning the top severity from all the dependency. 
        // This top severity that is returned, is the project severity.
        private Severity GetTopComponentSeverity(Severity topSeverity, Component depComponent)
        {
            if (depComponent != null)
            {
                topSeverity = Util.GetTopSeverity(topSeverity, depComponent.TopSeverity);
                // Adding to the data service components the dependencies itself.
                if (!getComponents().ContainsKey(depComponent.Key))
                {
                    getComponents().Add(depComponent.Key, depComponent);
                }
            }
            return topSeverity;
        }

        public async Task<Artifacts> GetSecurityIssuesAsync(bool reScan, Projects projects, string wd)
        {
            if (!reScan)
            {
                var componentsSet = new HashSet<Components>();
                foreach (NugetProject nugetProject in projects.projects)
                {
                    if (nugetProject.dependencies != null && nugetProject.dependencies.Length > 0)
                    {
                        // Get project's components which are not included in the cache.
                        componentsSet.UnionWith(Util.GetNoCachedComponents(nugetProject.dependencies, GetComponentsCache()));
                        // Update cache with new components.
                        GetComponentsCache().UnionWith(componentsSet);
                    }
                }
                // No change to the project dependencies, and a re-scan was not requested - returns the cached results.
                if (!componentsSet.Any())
                {
                    return GetArtifacts();
                }
            }
            var scanResuls = await ScanManager.Instance.PreformScanAsync(wd);
            var artifacts = await ParseCliAuditJsonAsync(scanResuls);
            // The return value of this function is never used, the data is saved due tothe intenal artifacts refrence.
            // Should be refactored to more maintanable and clear flow.
            ClearAllComponents();
            GetArtifacts().artifacts.AddRange(artifacts);
            return null;
        }

        public void ClearAllComponents()
        {
            GetComponentsCache().Clear();
            GetArtifacts().artifacts.Clear();
        }

        public Component getComponent(string key)
        {
            if (components.ContainsKey(key))
            {
                return components[key];
            }
            return new Component();
        }

        public Dictionary<string, Component> getComponents()
        {
            return components;
        }

        private void InitializeComponent()
        {
            this.RootElements = new List<string> {};
            this.components = new Dictionary<string, Component>();
            this.componentsCache = new HashSet<Components>();
        }

        private async Task<IEnumerable<Artifact>> ParseCliAuditJsonAsync(string scanResults)
        {
            var artifacts = new  Dictionary<string,Artifact>();
            var auditResults = JsonConvert.DeserializeObject<List<AuditResults>>(scanResults);
            foreach (var securityIssue in auditResults.First().AllSecurityIssues)
            {
                foreach (var entry in securityIssue.Components) {
                    var artifactId = GetIdWithoutPackagePrefix(entry.Key);
                    var directDependencyId = GetIdWithoutPackagePrefix(entry.Value.ImpactPaths[0][0].ComponentId);
                    Artifact artifact;
                    if (artifacts.ContainsKey(artifactId))
                    {
                        artifact = artifacts[artifactId];
                    } else
                    {
                        artifact = new Artifact
                        {
                            ArtifactId = artifactId,
                        };
                        artifacts.Add(artifactId, artifact);
                    }
                    await OutputLog.ShowMessageAsync($"Issue {securityIssue.Severity}, {securityIssue.Summary}, {directDependencyId}, {string.Join(" ", entry.Value.FixedVersions)}");
                    var issueType = string.IsNullOrEmpty(securityIssue.IssueType) ? "security" : securityIssue.IssueType;
                    var issue = new Issue(securityIssue.Severity, securityIssue.Summary, issueType, directDependencyId, string.Join(" ",entry.Value.FixedVersions));
                    artifact.Issues.Add(issue);
                }
            }
            return artifacts.Values.ToList();
        }

        private string GetIdWithoutPackagePrefix(string raw)
        {
            var separator = "://";
            var separatorIndex = raw.IndexOf(separator);
            if (separatorIndex != -1)
            {
                return raw.Substring(separatorIndex + separator.Length);
            }
            return raw;
        }
    }

    public enum RefreshType
    {
        Hard, Soft, None
    }

}
