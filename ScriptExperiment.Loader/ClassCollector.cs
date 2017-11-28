using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ScriptExperiment.Loader
{
    class ClassCollector : CSharpSyntaxWalker
    {
        public readonly List<string> Classes = new List<string>();

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            Classes.Add(((NamespaceDeclarationSyntax)node.Parent).Name + "." + node.Identifier.ToString());
            base.VisitClassDeclaration(node);
        }
    }
}
