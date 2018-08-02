
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using YamlDotNet.Serialization;

namespace HelmDeploy.Sdk.Tasks
{
    public class GenerateHelmApplicationFiles : Task
    {
        [Required]
        public string ProjectDirectory
        { get; set; }

        [Required]
        public string TargetDirectory
        { get; set; }

        [Required]
        public string ApplicationName
        { get; set; }

        [Required]
        public string ApplicationVersion
        { get; set; }

        [Required]
        public string ApplicationDescription
        { get; set; }

        public string RepositoryPrefix
        { get; set; }

        public ITaskItem[] ProjectReferences
        { get; set; }

        public override bool Execute()
        {
            try
            {
                CreateHelmIgnoreFile(new DirectoryInfo(TargetDirectory));
                CreateHelmChartFile(new FileInfo(Path.Combine(TargetDirectory, "Chart.yaml")), ApplicationName, ApplicationDescription);
                CreateYamlFileFromObject(
                    new FileInfo(Path.Combine(TargetDirectory, "values.yaml")),
                    new
                    {
                        global = new
                        {
                            application = new
                            {
                                name = ApplicationName,
                                version = ApplicationVersion
                            },
                            repositoryPrefix = !string.IsNullOrEmpty(RepositoryPrefix) ? $"{RepositoryPrefix}/" : null
                        }
                    }
                );

                if (ProjectReferences != null && ProjectReferences.Length > 0)
                {
                    Directory.CreateDirectory(Path.Combine(TargetDirectory, "charts"));

                    foreach (var projectReference in ProjectReferences)
                    {
                        CreateServiceHelmChart(projectReference);
                    }
                }

                return true;
            }
            catch (Exception err)
            {
                Log.LogErrorFromException(err);

                return false;
            }
        }

        private void CreateServiceHelmChart(ITaskItem projectReference)
        {
            var serviceProjectDirectory = new DirectoryInfo(Path.GetDirectoryName(projectReference.ItemSpec));
            var serviceName = serviceProjectDirectory.Name.Replace('.', '-').ToLowerInvariant();
            var serviceDescription = $"A Helm deployment chart for the '{serviceName}' service.";
            var serviceConfigDirectory = new DirectoryInfo(Path.Combine(ProjectDirectory, serviceProjectDirectory.Name));
            var serviceTargetDirectory = new DirectoryInfo(Path.Combine(TargetDirectory, "charts", serviceName));

            serviceTargetDirectory.Create();

            var serviceTemplatesDirectory = serviceTargetDirectory.CreateSubdirectory("templates");

            var configFile = new FileInfo(Path.Combine(serviceConfigDirectory.FullName, "config.yaml"));

            var config = LoadYamlFile<IDictionary<string, object>>(configFile);

            if (config == null) throw new ApplicationException($"Could not find a 'config.yaml' file for service '{serviceName}' ({serviceProjectDirectory})'.");

            var portsConfig = config.ContainsKey("ports")
                ? (List<object>)config["ports"]
                : Enumerable.Empty<object>().ToList();

            var serviceConfig = config.ContainsKey("service")
                ? (Dictionary<object, object>)config["service"]
                : null;

            if (serviceConfig != null && portsConfig.Count == 0)
            {
                throw new ApplicationException($"Invalid configuration for service '{serviceName}' ({configFile.FullName}). 'service' section requires 'ports' section.");
            }

            var servicePortsConfig = serviceConfig != null && serviceConfig.ContainsKey("ports")
                ? (List<object>)serviceConfig["ports"]
                : Enumerable.Empty<object>().ToList();

            CreateHelmChartFile(new FileInfo(Path.Combine(serviceTargetDirectory.FullName, "Chart.yaml")), serviceName, serviceDescription);

            CreateYamlFileFromObject(
                new FileInfo(Path.Combine(serviceTemplatesDirectory.FullName, "deployment.yaml")),
                new
                {
                    apiVersion = "apps/v1beta2",
                    kind = "Deployment",
                    metadata = new
                    {
                        name = $"{serviceName}-deployment",
                        labels = new
                        {
                            app = $"{ApplicationName}-{{{{ .Values.global.application.version }}}}"
                        }
                    },
                    spec = new
                    {
                        replicas = config.ContainsKey("replicaCount") ? config["replicaCount"] : 1,
                        selector = new
                        {
                            matchLabels = new Dictionary<string, object>()
                            {
                                { "app", ApplicationName }
                            }
                        },
                        template = new
                        {
                            metadata = new
                            {
                                labels = new Dictionary<string, object>()
                                {
                                    { "app", ApplicationName }
                                }
                            },
                            spec = new
                            {
                                containers = new List<IDictionary<string, object>>()
                                {
                                    new Dictionary<string, object>()
                                    {
                                        { "name", serviceName },
                                        { "image", $"{{{{ .Values.global.repositoryPrefix }}}}{ApplicationName}/{serviceName}:{{{{ .Values.global.application.version }}}}" },
                                        { "imagePullPolicy",  "IfNotPresent"},
                                        {
                                            "ports",
                                            portsConfig.Select(
                                                (dynamic port) =>
                                                {
                                                    if (!port.ContainsKey("port")) throw new ApplicationException($"Invalid configuration for service '{serviceName}' ({configFile.FullName}). A port requires a 'port' value.");

                                                    var newPort = new Dictionary<string, object>();
                                                    
                                                    if (port.ContainsKey("name")) newPort["name"] = port["name"];
                                                    if (port.ContainsKey("protocol")) newPort["protocol"] = port["protocol"];

                                                    newPort["containerPort"] = port["port"];

                                                    return newPort;
                                                }
                                            )
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            );

            CreateYamlFileFromObject(
                new FileInfo(Path.Combine(serviceTemplatesDirectory.FullName, "service.yaml")),
                new
                {
                    apiVersion = "v1",
                    kind = "Service",
                    metadata = new
                    {
                        name = $"{serviceName}",
                        labels = new
                        {
                            app = $"{ApplicationName}-{{{{ .Values.global.application.version }}}}"
                        }
                    },
                    spec = new
                    {
                        ports = servicePortsConfig.Select(
                            (dynamic port) =>
                            {
                                if (!port.ContainsKey("port")) throw new ApplicationException($"Invalid configuration for service '{serviceName}' ({configFile.FullName}). A service port requires a 'port' value.");

                                var newPort = new Dictionary<string, object>();

                                if (port.ContainsKey("name")) newPort["name"] = port["name"];
                                if (port.ContainsKey("protocol")) newPort["protocol"] = port["protocol"];

                                newPort["port"] = port["port"];

                                if (port.ContainsKey("targetPort"))
                                {
                                    var portNo = 0;

                                    if (!int.TryParse(port["targetPort"], out portNo))
                                    {
                                        // targetPort is a string reference, check that the name exists in the configured ports
                                        if (portsConfig.FirstOrDefault((dynamic p) => p.ContainsKey("name") && p["name"] == port["targetPort"]) == null)
                                        {
                                            throw new ApplicationException($"Invalid configuration for service '{serviceName}' ({configFile.FullName}). A service port with a 'targetPort' value of '{port["targetPort"]}' is referencing an unknown port.");
                                        }
                                    }

                                    newPort["targetPort"] = port["targetPort"];
                                }
                                else
                                {
                                    newPort["targetPort"] = port["port"];
                                }

                                return newPort;
                            }
                        ),
                        selector = new
                        {
                            app = ApplicationName
                        }
                    }
                }
            );

            var ingressPathsConfig = servicePortsConfig.Where((dynamic port) => port.ContainsKey("ingress"))
                .Select((dynamic port) =>
                    {
                        var path = new Dictionary<string, object>()
                        {
                            { "path", port["ingress"].ContainsKey("path") ? port["ingress"]["path"] : "/" },
                            { 
                                "backend",
                                new Dictionary<string, object>()
                                { 
                                    { "serviceName", $"{serviceName}" },
                                    { "servicePort", port["port"] }
                                }
                            }
                        };

                        if (port["ingress"].ContainsKey("host"))
                        {
                            path.Add("host", port["ingress"]["host"]);
                        }

                        return path;
                    }
                )
                .ToList();

            if (ingressPathsConfig.Count == 0) return;

            CreateYamlFileFromObject(
                new FileInfo(Path.Combine(serviceTemplatesDirectory.FullName, "ingress.yaml")),
                new
                {
                    apiVersion = "extensions/v1beta1",
                    kind = "Ingress",
                    metadata = new
                    {
                        name = $"{serviceName}-ingress",
                        labels = new
                        {
                            app = $"{ApplicationName}-{{{{ .Values.global.application.version }}}}"
                        },
                        annotations = new Dictionary<string, object>()
                        {
                            { "kubernetes.io/ingress.class", "nginx" }
                        }
                    },
                    spec = new
                    {
                        rules = new List<Dictionary<string, object>>()
                        {
                            new Dictionary<string, object>()
                            {
                                {
                                    "http",
                                    new { paths = ingressPathsConfig }
                                }
                            }
                        }
                    }
                }
            );
        }

        private T LoadYamlFile<T>(FileInfo sourceFile) where T : class
        {
            if (!sourceFile.Exists) return null;

            var deserializer = new DeserializerBuilder().Build();

            using (var reader = new StreamReader(sourceFile.FullName))
            {
                return deserializer.Deserialize<T>(reader);
            }
        }

        private void CreateHelmChartFile(FileInfo targetFile, string chartName, string chartDescription)
        {
            CreateYamlFileFromObject(
                targetFile,
                new
                {
                    apiVersion = "v1",
                    name = chartName,
                    version = ApplicationVersion,
                    description = chartDescription
                }
            );
        }

        private void CreateYamlFileFromObject(FileInfo targetFile, object values)
        {
            var serializer = new SerializerBuilder().Build();

            if (targetFile.Exists)
            {
                targetFile.Delete();
            }

            using (var writer = File.CreateText(targetFile.FullName))
            {
                serializer.Serialize(writer, values);
            }
        }

        private void CreateHelmIgnoreFile(DirectoryInfo targetDirectory)
        {
            using (var writer = File.CreateText(Path.Combine(targetDirectory.FullName, ".helmignore")))
            {
                writer.WriteLine("# Patterns to ignore when building packages.");
                writer.WriteLine(".DS_Store");
            }
        }
    }
}
