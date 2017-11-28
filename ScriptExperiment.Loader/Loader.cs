using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using System.IO;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Diagnostics;
using System.Threading;
using System.Collections.Concurrent;

namespace ScriptExperiment.Loader
{
    public class Script
    {
        public string FilePath { get; set; }
        public string ClassName { get; set; }
        public string FullClassName { get; set; }
        public Assembly Assembly { get; set; }
        public Type Type { get; set; }
        public object Instance { get; set; }
        public DateTime LastCompilationTime { get; set; }
    }

    public sealed class ScriptLoader
    {
        private static readonly Lazy<ScriptLoader> lazy = new Lazy<ScriptLoader>(() => new ScriptLoader());

        public static ScriptLoader Instance { get { return lazy.Value; } }

        private ScriptLoader()
        {
            cancelTokenSource = new CancellationTokenSource();
            filesToCompile = new BlockingCollection<string>();
            scriptFolderLocations = new List<string>();
            scripts = new List<Script>();
            fileSystemWatchers = new List<FileSystemWatcher>();
            lastCompilationRequestTimes = new Dictionary<string, DateTime>();

            scriptCompilationTask = new Task(() => ScriptCompilationTask(cancelTokenSource.Token, filesToCompile));
            scriptCompilationTask.Start();
        }

        private CancellationTokenSource cancelTokenSource;
        private BlockingCollection<string> filesToCompile;
        private List<string> scriptFolderLocations;
        private List<Script> scripts;
        private List<FileSystemWatcher> fileSystemWatchers;
        private Dictionary<string, DateTime> lastCompilationRequestTimes;

        private Task scriptCompilationTask;

        public void AddFolderLocation(string folderLocation)
        {
            scriptFolderLocations.Add(folderLocation);
            var files = Directory.GetFiles(folderLocation, "*.cs");
            foreach (var file in files)
                filesToCompile.Add(file);

            FileSystemWatcher watcher = new FileSystemWatcher
            {
                Path = folderLocation,
                NotifyFilter = NotifyFilters.LastWrite,
                Filter = "*.cs"
            };
            watcher.Changed += new FileSystemEventHandler(OnChanged);           //To do - check whether event is raised multiple times
            watcher.EnableRaisingEvents = true;
            fileSystemWatchers.Add(watcher);
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            if (lastCompilationRequestTimes.ContainsKey(e.FullPath))
            {
                if(lastCompilationRequestTimes[e.FullPath].AddSeconds(1) < DateTime.UtcNow)
                {
                    filesToCompile.Add(e.FullPath);
                    lastCompilationRequestTimes[e.FullPath] = DateTime.UtcNow;
                }                  
            }
            else
            {
                filesToCompile.Add(e.FullPath);
                lastCompilationRequestTimes[e.FullPath] = DateTime.UtcNow;
            }
        }

        public void RunMethod(string scriptClass, string method, object[] parameters)
        {
            if (scripts.Count(s => s.ClassName == scriptClass) == 1)
            {
                var script = scripts.First(s => s.ClassName == scriptClass);

                Console.WriteLine("Running method '" + method + "'");
                script.Type.InvokeMember(method,
                    BindingFlags.Default | BindingFlags.InvokeMethod,
                    null,
                    script.Instance,
                    parameters);
            }
        }

        private FileStream WaitForFile(string fullPath, FileMode mode, FileAccess access, FileShare share)
        {
            for (int numTries = 0; numTries < 10; numTries++)
            {
                FileStream fs = null;
                try
                {
                    fs = new FileStream(fullPath, mode, access, share);
                    return fs;
                }
                catch (IOException)
                {
                    if (fs != null)
                    {
                        fs.Dispose();
                    }
                    Thread.Sleep(50);
                }
            }

            return null;
        }

        private void ScriptCompilationTask(CancellationToken cancelToken, BlockingCollection<string> queue)
        {
            while (!cancelToken.IsCancellationRequested)
            {
                if (queue.TryTake(out string filePath, 500))
                {
                    Script script = new Script
                    {
                        FilePath = filePath
                    };

                    Console.WriteLine("Reading file");
                    var fileStream = WaitForFile(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);      //FileSystemWatcher doesn't wait for the file handle to be released!
                    string fileContents;
                    using (var sr = new StreamReader(fileStream))
                    {
                        fileContents = sr.ReadToEnd();
                    }
                    fileStream.Dispose();
                    Console.WriteLine("Parse syntax");
                    SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(fileContents);

                    var root = (CompilationUnitSyntax)syntaxTree.GetRoot();
                    var collector = new ClassCollector();
                    collector.Visit(root);
                    if (collector.Classes.Count == 0)
                    {
                        Console.Error.WriteLine("No class found in script");
                        return;
                    }
                    if (collector.Classes.Count > 1)
                    {
                        Console.Error.WriteLine("Only 1 class allowed in script");
                        return;
                    }

                    script.ClassName = collector.Classes[0].Split('.').Last();      //Replace this with something better later... we already know the proper class name inside ClassCollector. Use it.
                    script.FullClassName = collector.Classes[0];

                    string assemblyName = Path.GetRandomFileName();
                    MetadataReference[] references = new MetadataReference[]
                    {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location)
                    };

                    CSharpCompilation compilation = CSharpCompilation.Create(
                        assemblyName,
                        syntaxTrees: new[] { syntaxTree },
                        references: references,
                        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                    using (var ms = new MemoryStream())
                    {
                        Console.WriteLine("Compiling...");
                        Stopwatch stopWatch = new Stopwatch();
                        stopWatch.Start();
                        EmitResult result = compilation.Emit(ms);
                        stopWatch.Stop();
                        Console.WriteLine("Compile complete. (" + stopWatch.ElapsedMilliseconds + "ms)");
                        script.LastCompilationTime = DateTime.UtcNow;
                        if (!result.Success)
                        {
                            IEnumerable<Diagnostic> failures = result.Diagnostics.Where(diagnostic =>
                                diagnostic.IsWarningAsError ||
                                diagnostic.Severity == DiagnosticSeverity.Error);

                            foreach (Diagnostic diagnostic in failures)
                            {
                                Console.Error.WriteLine("{0}: {1}", diagnostic.Id, diagnostic.GetMessage());
                            }
                        }
                        else
                        {
                            Console.WriteLine("Loading assembly...");
                            ms.Seek(0, SeekOrigin.Begin);
                            Assembly assembly = Assembly.Load(ms.ToArray());
                            script.Assembly = assembly;
                            Console.WriteLine("Assembly loaded.");

                            Console.WriteLine("Activating " + script.FullClassName + "...");
                            Type type = assembly.GetType(script.FullClassName);
                            script.Type = type;
                            object obj = Activator.CreateInstance(type);
                            script.Instance = obj;
                            Console.WriteLine("Activated.");

                            scripts.RemoveAll(s => s.FullClassName == script.FullClassName);        //To do - add locks around this for concurrency safety
                            scripts.Add(script);
                        }
                    }
                }
            }
        }
    }
}
