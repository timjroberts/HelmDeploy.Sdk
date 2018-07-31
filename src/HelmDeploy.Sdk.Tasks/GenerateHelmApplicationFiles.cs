
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

        private void CreateServiceHelmChart(ITaskItem projectReference)
        {
            var serviceProjectDirectory = new DirectoryInfo(Path.GetDirectoryName(projectReference.ItemSpec));
            var serviceName = serviceProjectDirectory.Name.Replace('.', '-').ToLowerInvariant();
            var serviceDescription = $"A Helm deployment chart for the '{serviceName}' service.";
            var serviceConfigDirectory = new DirectoryInfo(Path.Combine(ProjectDirectory, serviceProjectDirectory.Name));
            var serviceTargetDirectory = new DirectoryInfo(Path.Combine(TargetDirectory, "charts", serviceName));

            serviceTargetDirectory.Create();

            var serviceTemplatesDirectory = serviceTargetDirectory.CreateSubdirectory("templates");

            CreateHelmChartFile(new FileInfo(Path.Combine(serviceTargetDirectory.FullName, "Chart.yaml")), serviceName, serviceDescription);

            var portsConfig = LoadYamlFile<IDictionary<string, IList<IDictionary<string, dynamic>>>>(new FileInfo(Path.Combine(serviceConfigDirectory.FullName, "Ports.yaml")));

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
                        replicas = 1,
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
                                            portsConfig["ports"].Select(
                                                port =>
                                                {
                                                    var newPort = new Dictionary<string, object>();

                                                    newPort["name"] = port["name"];
                                                    newPort["protocol"] = port["protocol"];
                                                    newPort["containerPort"] = port["port"];

                                                    if (port.ContainsKey("targetPort")) newPort["targetPort"] = port["targetPort"];

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
                        name = $"{serviceName}-service",
                        labels = new
                        {
                            app = $"{ApplicationName}-{{{{ .Values.global.application.version }}}}"
                        }
                    },
                    spec = new
                    {
                        ports = portsConfig["ports"].Select(
                            port =>
                            {
                                var newPort = new Dictionary<string, object>();

                                newPort["name"] = port["name"];
                                newPort["protocol"] = port["protocol"];
                                newPort["port"] = port["port"];

                                if (port.ContainsKey("targetPort")) newPort["targetPort"] = port["targetPort"];

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

            if (portsConfig == null) return;

            var httpPortsConfig = portsConfig["ports"].Where(
                port => string.Equals(port["name"], "http", StringComparison.InvariantCultureIgnoreCase) || string.Equals(port["name"], "https", StringComparison.InvariantCultureIgnoreCase)
            ).ToList();

            if (httpPortsConfig == null || httpPortsConfig.Count == 0) return;

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
                        rules = httpPortsConfig.Select(
                            port =>
                            {
                                var mapping = new Dictionary<string, object>()
                                {
                                    {
                                        port["name"],
                                        new { paths = new List<IDictionary<string, object>>()
                                            {
                                                new Dictionary<string, object>()
                                                {
                                                    { "path", port.ContainsKey("path") ? port["path"] : "/" },
                                                    { 
                                                        "backend",
                                                        new Dictionary<string, object>()
                                                        { 
                                                            { "serviceName", $"{serviceName}-service" },
                                                            { "servicePort", port["port"] }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                };

                                if (port.ContainsKey("host"))
                                {
                                    mapping.Add("host", port["host"]);
                                }

                                return mapping;
                            }
                        ).ToList()
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
