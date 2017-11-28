using ScriptExperiment.Loader;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ScriptExperiment.ConsoleApp
{
    class Program
    {
        static void Main(string[] args)
        {
            ScriptLoader.Instance.AddFolderLocation("Scripts");

            Console.ReadLine();

            ScriptLoader.Instance.RunMethod("ScriptA", "Write", new object[] { "Hello World." });

            Console.Read();
        }
    }
}
